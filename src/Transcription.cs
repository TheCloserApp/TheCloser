using System;

namespace TheCloser
{
    internal sealed class TranscriptEvent
    {
        public readonly string Speaker;
        public readonly string Text;
        public readonly bool IsFinal;

        public TranscriptEvent(string speaker, string text, bool isFinal)
        {
            Speaker = speaker;
            Text = text;
            IsFinal = isFinal;
        }
    }

    internal interface ITranscriptSource : IDisposable
    {
        event Action<TranscriptEvent> Transcript;
        event Action<string> Status;
        event Action<string, bool> Activity; // speaker, is speaking
        string Name { get; }
        void Start();
        void Stop();
    }
}
