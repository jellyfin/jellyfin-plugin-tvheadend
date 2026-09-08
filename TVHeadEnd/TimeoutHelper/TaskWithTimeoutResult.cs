using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TVHeadEnd.TimeoutHelper
{
    public class TaskWithTimeoutResult<T>
    {
        public T Result { get; set; } = default!;

        public bool HasTimeout { get; set; }
    }
}
