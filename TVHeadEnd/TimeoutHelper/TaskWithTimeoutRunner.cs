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
                            myTaskResult.Result = task.Result;
                            myTaskResult.HasTimeout = false;
                            return myTaskResult;
                        },
                        TaskCreationOptions.LongRunning);

                    longRunningTask.Start();

                    if (longRunningTask.Wait(_timeout))
                    {
                        return longRunningTask.Result;
                    }

                    // If we reach here we had an timeout
                    TaskWithTimeoutResult<T> timeoutResult = new TaskWithTimeoutResult<T>();
                    timeoutResult.Result = default!;
                    timeoutResult.HasTimeout = true;
                    return timeoutResult;
                });

                outherTask.Start();
                outherTask.Wait();
                return outherTask.Result;
            });
        }
    }
}
