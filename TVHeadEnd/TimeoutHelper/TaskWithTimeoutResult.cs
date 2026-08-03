using System;

namespace TVHeadEnd.TimeoutHelper
{
    public class TaskWithTimeoutResult<T>
    {
        public T Result { get; set; } = default!;

        public bool HasTimeout { get; set; }
    }
}
