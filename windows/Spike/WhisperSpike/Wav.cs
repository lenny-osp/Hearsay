namespace WhisperSpike;

/// <summary>Minimal RIFF reader for the fixtures: 16 kHz mono s16le only.</summary>
internal static class Wav
{
    public static float[] ReadMono16k(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
        {
            throw new InvalidDataException($"{path}: not a RIFF/WAVE file");
        }
        int pos = 12;
        int channels = 0, rate = 0, bits = 0;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToInt16(b, body + 14);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != 16000 || bits != 16)
                {
                    throw new InvalidDataException($"{path}: need 16 kHz mono s16le, got {rate} Hz {channels} ch {bits} bit");
                }
                int n = Math.Min(size, b.Length - body) / 2;
                var s = new float[n];
                for (int i = 0; i < n; i++)
                {
                    s[i] = BitConverter.ToInt16(b, body + 2 * i) / 32768f;
                }
                return s;
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }
}
