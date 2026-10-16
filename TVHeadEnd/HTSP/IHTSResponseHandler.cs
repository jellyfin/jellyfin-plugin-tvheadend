using System;

namespace TVHeadEnd.HTSP
{
    public interface IHTSResponseHandler
    {
        void HandleResponse(HTSMessage response);

        void HandleError(Exception error);
    }
}
