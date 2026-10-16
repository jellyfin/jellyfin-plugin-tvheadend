using System;
using System.Threading.Tasks;

namespace TVHeadEnd.TimeoutHelper
{
    public class TaskWithTimeoutRunner<T>
    {
        private readonly TimeSpan _timeout;

        public TaskWithTimeoutRunner(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        public Task<TaskWithTimeoutResult<T>> RunWithTimeout(Task<T> task)
        {
            return Task.Run(() =>
            {
                Task<TaskWithTimeoutResult<T>> outherTask = new Task<TaskWithTimeoutResult<T>>(() =>
                {
                    Task<TaskWithTimeoutResult<T>> longRunningTask = new Task<TaskWithTimeoutResult<T>>(
                        () =>
                        {
                            TaskWithTimeoutResult<T> myTaskResult = new TaskWithTimeoutResult<T>();
                            // GetResult() rethrows the task's own exception instead of wrapping
                            // it in AggregateException, so cancellation and connection errors
                            // keep their type all the way up to Jellyfin.
                            myTaskResult.Result = task.GetAwaiter().GetResult();
                            myTaskResult.HasTimeout = false;
                            return myTaskResult;
                        },
                        TaskCreationOptions.LongRunning);

                    longRunningTask.Start();

                    bool completed;
                    try
                    {
                        completed = longRunningTask.Wait(_timeout);
                    }
                    catch (AggregateException)
                    {
                        completed = true;
                    }

                    if (completed)
                    {
                        return longRunningTask.GetAwaiter().GetResult();
                    }

                    // If we reach here we had an timeout
                    TaskWithTimeoutResult<T> timeoutResult = new TaskWithTimeoutResult<T>();
                    timeoutResult.Result = default!;
                    timeoutResult.HasTimeout = true;
                    return timeoutResult;
                });

                outherTask.Start();
                return outherTask.GetAwaiter().GetResult();
            });
        }
    }
}
