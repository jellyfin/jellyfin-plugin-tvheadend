using System;

namespace TVHeadEnd.HTSP
{
    public interface IHTSConnectionListener
    {
        void OnMessage(HTSConnectionAsync connection, HTSMessage response);

        /// <summary>
        /// Reports a fatal error of one connection.
        /// </summary>
        /// <param name="connection">The connection the error occurred on, so that a stale
        /// connection's death cannot tear down its replacement.</param>
        /// <param name="ex">The error.</param>
        void OnError(HTSConnectionAsync connection, Exception ex);
    }
}
