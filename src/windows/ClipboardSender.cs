using System;
namespace AtomClipUsb
{
    // A nonblocking outbound state machine shared by the system host and tests.
    public sealed class ClipboardSender
    {
        readonly byte[] key;
        uint sequence;
        byte[] frame;
        string id;
        int attempts;
        DateTime retry;
        public bool Enabled;
        public int Confirmed;
        public ClipboardSender(byte[] key) { this.key = key; }
        public void Reset(uint current) { sequence = current; frame = null; id = null; }
        public void Acknowledge(byte[] payload)
        { if (id != null && Protocol.ReadAck(payload, key) == id) { frame = null; id = null; Confirmed++; } }
        public void Poll(uint current, bool ready, Func<string> read, Action<byte[]> send, DateTime now)
        {
            if (!Enabled || !ready) { Reset(current); return; }
            if (current != sequence)
            {
                // Leave the sequence pending if another application owns the clipboard.
                string text = read();
                if (text != null)
                {
                    byte[] payload = Protocol.Encrypt(text, key);
                    frame = Protocol.Frame(Protocol.Data, payload); id = Protocol.Id(payload);
                    attempts = 0; retry = DateTime.MinValue;
                }
                else { frame = null; id = null; }
                sequence = current;
            }
            if (frame == null || now < retry) return;
            if (attempts >= 3) { frame = null; id = null; Console.WriteLine("发送未确认，请重新复制。 "); return; }
            send(frame); attempts++; retry = now.AddSeconds(12);
        }
    }
}
