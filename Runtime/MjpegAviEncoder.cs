using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MacacaGames.RuntimeBugReporter
{
    internal static class MjpegAviEncoder
    {
        public static byte[] Encode(IReadOnlyList<byte[]> jpegFrames, int width, int height, int framesPerSecond)
        {
            if (jpegFrames == null || jpegFrames.Count == 0)
                return null;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                WriteFourCc(writer, "RIFF");
                var riffSizePosition = stream.Position;
                writer.Write(0);
                WriteFourCc(writer, "AVI ");

                WriteFourCc(writer, "LIST");
                var headerListSizePosition = stream.Position;
                writer.Write(0);
                WriteFourCc(writer, "hdrl");

                WriteFourCc(writer, "avih");
                writer.Write(56);
                var maxFrameSize = 0;
                foreach (var frame in jpegFrames)
                    maxFrameSize = Math.Max(maxFrameSize, frame == null ? 0 : frame.Length);
                writer.Write(1000000 / Math.Max(1, framesPerSecond));
                writer.Write(maxFrameSize * Math.Max(1, framesPerSecond));
                writer.Write(0);
                writer.Write(0x10);
                writer.Write(jpegFrames.Count);
                writer.Write(0);
                writer.Write(1);
                writer.Write(maxFrameSize);
                writer.Write(width);
                writer.Write(height);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

                WriteFourCc(writer, "LIST");
                var streamListSizePosition = stream.Position;
                writer.Write(0);
                WriteFourCc(writer, "strl");
                WriteFourCc(writer, "strh");
                writer.Write(56);
                WriteFourCc(writer, "vids");
                WriteFourCc(writer, "MJPG");
                writer.Write(0);
                writer.Write((short)0);
                writer.Write((short)0);
                writer.Write(0);
                writer.Write(1);
                writer.Write(Math.Max(1, framesPerSecond));
                writer.Write(0);
                writer.Write(jpegFrames.Count);
                writer.Write(maxFrameSize);
                writer.Write(-1);
                writer.Write(0);
                writer.Write((short)0); writer.Write((short)0);
                writer.Write((short)width); writer.Write((short)height);

                WriteFourCc(writer, "strf");
                writer.Write(40);
                writer.Write(40);
                writer.Write(width);
                writer.Write(height);
                writer.Write((short)1);
                writer.Write((short)24);
                WriteFourCc(writer, "MJPG");
                writer.Write(width * height * 3);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

                PatchSize(writer, streamListSizePosition, stream.Position - streamListSizePosition - 4);
                PatchSize(writer, headerListSizePosition, stream.Position - headerListSizePosition - 4);

                WriteFourCc(writer, "LIST");
                var movieListSizePosition = stream.Position;
                writer.Write(0);
                WriteFourCc(writer, "movi");
                var index = new List<IndexEntry>(jpegFrames.Count);
                var movieDataStart = stream.Position;
                foreach (var frame in jpegFrames)
                {
                    if (frame == null || frame.Length == 0)
                        continue;
                    var chunkStart = stream.Position;
                    WriteFourCc(writer, "00dc");
                    writer.Write(frame.Length);
                    writer.Write(frame);
                    if ((frame.Length & 1) != 0)
                        writer.Write((byte)0);
                    index.Add(new IndexEntry((int)(chunkStart - movieDataStart + 4), frame.Length));
                }
                PatchSize(writer, movieListSizePosition, stream.Position - movieListSizePosition - 4);

                WriteFourCc(writer, "idx1");
                writer.Write(index.Count * 16);
                foreach (var entry in index)
                {
                    WriteFourCc(writer, "00dc");
                    writer.Write(0x10);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Length);
                }

                PatchSize(writer, riffSizePosition, stream.Length - 8);
                return stream.ToArray();
            }
        }

        private static void WriteFourCc(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));

        private static void PatchSize(BinaryWriter writer, long position, long size)
        {
            var current = writer.BaseStream.Position;
            writer.BaseStream.Position = position;
            writer.Write((int)size);
            writer.BaseStream.Position = current;
        }

        private struct IndexEntry
        {
            public readonly int Offset;
            public readonly int Length;
            public IndexEntry(int offset, int length) { Offset = offset; Length = length; }
        }
    }
}
