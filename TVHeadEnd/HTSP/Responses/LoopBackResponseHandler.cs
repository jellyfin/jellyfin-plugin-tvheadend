using System;
using TVHeadEnd.Helper;

namespace TVHeadEnd.HTSP.Responses
{
    public class LoopBackResponseHandler : IHTSResponseHandler
    {
        private readonly BlockingBuffer<HTSMessage> _responseDataQueue;

        public LoopBackResponseHandler()
        {
            _responseDataQueue = new BlockingBuffer<HTSMessage>(1);
        }

        public void HandleResponse(HTSMessage response)
        {
            _responseDataQueue.Enqueue(response);
        }

        public void HandleError(Exception error)
        {
            _responseDataQueue.Close(error);
        }

        public HTSMessage GetResponse()
        {
            return _responseDataQueue.Dequeue();
        }

        public HTSMessage? GetResponse(TimeSpan timeout)
        {
            return _responseDataQueue.TryDequeue(out HTSMessage? response, timeout) ? response : null;
        }
    }
}
