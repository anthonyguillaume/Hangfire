﻿// This file is part of Hangfire.
// Copyright © 2015 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Processing;

namespace Hangfire.Server
{
    internal class CoreBackgroundJobPerformer : IBackgroundJobPerformer
    {
        internal static readonly Dictionary<Type, Func<PerformContext, object>> Substitutions
            = new Dictionary<Type, Func<PerformContext, object>>
            {
                { typeof (IJobCancellationToken), x => x.CancellationToken },
                { typeof (CancellationToken), x => x.CancellationToken.ShutdownToken },
                { typeof (PerformContext), x => x }
            };

        private readonly JobActivator _activator;
        private readonly TaskScheduler _taskScheduler;

        public CoreBackgroundJobPerformer([NotNull] JobActivator activator, [CanBeNull] TaskScheduler taskScheduler)
        {
            _activator = activator ?? throw new ArgumentNullException(nameof(activator));
            _taskScheduler = taskScheduler;
        }

        public object Perform(PerformContext context)
        {
            using (var scope = _activator.BeginScope(context))
            {
                object instance = null;

                if (context.BackgroundJob.Job == null)
                {
                    throw new InvalidOperationException("Can't perform a background job with a null job.");
                }
                
                if (!context.BackgroundJob.Job.Method.IsStatic)
                {
                    instance = scope.Resolve(context.BackgroundJob.Job.Type);

                    if (instance == null)
                    {
                        throw new InvalidOperationException(
                            $"JobActivator returned NULL instance of the '{context.BackgroundJob.Job.Type}' type.");
                    }
                }

                var arguments = SubstituteArguments(context);
                var result = InvokeMethod(context, instance, arguments);

                return result;
            }
        }

        internal static void HandleJobPerformanceException(Exception exception, IJobCancellationToken cancellationToken)
        {
            if (exception is JobAbortedException)
            {
                // JobAbortedException exception should be thrown as-is to notify
                // a worker that background job was aborted by a state change, and
                // should NOT be re-queued.
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
            
            if (exception is OperationCanceledException && cancellationToken.IsAborted())
            {
                // OperationCanceledException exception is thrown because 
                // ServerJobCancellationWatcher has detected the job was aborted.
                throw new JobAbortedException();
            }

            if (exception is OperationCanceledException && cancellationToken.ShutdownToken.IsCancellationRequested)
            {
                // OperationCanceledException exceptions are treated differently from
                // others, when ShutdownToken's cancellation was requested, to notify
                // a worker that job performance was aborted by a shutdown request,
                // and a job identifier should BE re-queued.
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw exception;
            }

            // Other exceptions are wrapped with JobPerformanceException to preserve a
            // shallow stack trace without Hangfire methods.
            throw new JobPerformanceException(
                "An exception occurred during performance of the job.",
                exception);
        }

        private object InvokeMethod(PerformContext context, object instance, object[] arguments)
        {
            if (context.BackgroundJob.Job == null) return null;

            try
            {
                var methodInfo = context.BackgroundJob.Job.Method;
                var tuple = Tuple.Create(methodInfo, instance, arguments);

                if (methodInfo.ReturnType.IsAwaitable(out var awaitable))
                {
                    if (_taskScheduler != null)
                    {
                        return InvokeOnTaskScheduler(context, tuple, awaitable);
                    }

                    return InvokeOnTaskPump(context, tuple, awaitable);
                }

                return InvokeSynchronously(tuple);
            }
            catch (ArgumentException ex)
            {
                HandleJobPerformanceException(ex, context.CancellationToken);
                throw;
            }
            catch (AggregateException ex)
            {
                HandleJobPerformanceException(ex.InnerException, context.CancellationToken);
                throw;
            }
            catch (TargetInvocationException ex)
            {
                HandleJobPerformanceException(ex.InnerException, context.CancellationToken);
                throw;
            }
        }

        private object InvokeOnTaskScheduler(PerformContext context, Tuple<MethodInfo, object, object[]> tuple, AwaitableContext awaitable)
        {
            var task = Task.Factory.StartNew(
                InvokeSynchronously,
                tuple,
                context.CancellationToken.ShutdownToken,
                TaskCreationOptions.None,
                _taskScheduler);

            var result = task.GetAwaiter().GetResult();
            if (result == null) return null;

            var awaiter = awaitable.GetAwaiter(result);
            return awaitable.GetResult(awaiter);
        }

        private static object InvokeOnTaskPump(PerformContext context, Tuple<MethodInfo, object, object[]> tuple, AwaitableContext awaitable)
        {
            // Using SynchronizationContext here is the best default option, where workers
            // are still running on synchronous dispatchers, and where a single job performer
            // may be used by multiple workers. We can't create a separate TaskScheduler
            // instance of every background job invocation, because TaskScheduler.Id may
            // overflow relatively fast, and can't use single scheduler for multiple performers
            // for better isolation in the default case – non-default external scheduler should
            // be used. It's also great to preserve backward compatibility for those who are
            // using Parallel.For(Each), since we aren't changing the TaskScheduler.Current.

            var oldSyncContext = SynchronizationContext.Current;

            try
            {
                using (var syncContext = new InlineSynchronizationContext())
                using (var cancellationEvent = context.CancellationToken.ShutdownToken.GetCancellationEvent())
                {
                    SynchronizationContext.SetSynchronizationContext(syncContext);

                    var result = InvokeSynchronously(tuple);
                    if (result == null) return null;

                    var awaiter = awaitable.GetAwaiter(result);
                    var waitHandles = new[] { syncContext.WaitHandle, cancellationEvent.WaitHandle };

                    while (!awaitable.IsCompleted(awaiter) && WaitHandle.WaitAny(waitHandles) == 0)
                    {
                        var workItem = syncContext.Dequeue();
                        workItem.Item1(workItem.Item2);
                    }

                    return awaitable.GetResult(awaiter);
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(oldSyncContext);
            }
        }

        private static object InvokeSynchronously(object state)
        {
            var data = (Tuple<MethodInfo, object, object[]>) state;
            return data.Item1.Invoke(data.Item2, data.Item3);
        }

        private static object[] SubstituteArguments(PerformContext context)
        {
            if (context.BackgroundJob.Job == null)
            {
                return null;
            }

            var parameters = context.BackgroundJob.Job.Method.GetParameters();
            var result = new List<object>(context.BackgroundJob.Job.Args.Count);

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var argument = context.BackgroundJob.Job.Args[i];

                var value = Substitutions.ContainsKey(parameter.ParameterType) 
                    ? Substitutions[parameter.ParameterType](context) 
                    : argument;

                result.Add(value);
            }

            return result.ToArray();
        }
    }
}