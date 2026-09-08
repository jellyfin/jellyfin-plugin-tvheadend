using System;

namespace TVHeadEnd.HTSP
{
    public interface IHTSConnectionListener
    {
        void OnMessage(HTSMessage response);

        void OnError(Exception ex);
    }
}
