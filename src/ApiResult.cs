using System;

namespace TheCloser
{
    /// <summary>An AI provider rejected a request (status, provider error type, readable message).</summary>
    internal sealed class ApiException : Exception
    {
        public readonly int Status;
        public readonly string ErrorType;

        public ApiException(int status, string type, string message) : base(message)
        {
            Status = status;
            ErrorType = type;
        }
    }

    /// <summary>What a streamed answer ended with.</summary>
    internal sealed class StreamResult
    {
        public string Text = "";
        public string StopReason;
        public string Model;
        public long InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens;
        public double FirstTokenSeconds, TotalSeconds;
    }
}
