using System;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.HTSP_Responses
{
    public class LoopBackResponseHandler : HTSResponseHandler
    {
        private readonly SizeQueue<HTSMessage> _responseDataQueue;

        public LoopBackResponseHandler()
        {
            _responseDataQueue = new SizeQueue<HTSMessage>(1);
        }

        public void handleResponse(HTSMessage response)
        {
            _responseDataQueue.Enqueue(response);
        }

        public HTSMessage getResponse()
        {
            return _responseDataQueue.Dequeue();
        }

        public HTSMessage getResponse(TimeSpan timeout)
        {
            HTSMessage response;
            return _responseDataQueue.TryDequeue(out response, timeout) ? response : null;
        }
    }
}
