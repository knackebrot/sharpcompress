using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar
{
    internal class MultiVolumeReadOnlyStream : Stream
    {
        private long currentPosition;
        private long maxPosition;
        private long maxOffset;

        private readonly IEnumerable<RarFilePart> fileParts;
        private IEnumerator<RarFilePart> filePartEnumerator;
        private Stream currentStream;

        private readonly IExtractionListener streamListener;

        private long currentPartTotalReadBytes;
        private long currentEntryTotalReadBytes;

        internal MultiVolumeReadOnlyStream(IEnumerable<RarFilePart> parts, IExtractionListener streamListener)
        {
            this.streamListener = streamListener;

            fileParts = parts;
            maxOffset = fileParts.Select(filePart => filePart.FileHeader.CompressedSize).Sum();
            filePartEnumerator = fileParts.GetEnumerator();
            filePartEnumerator.MoveNext();
            InitializeNextFilePart();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                if (filePartEnumerator != null)
                {
                    filePartEnumerator.Dispose();
                    filePartEnumerator = null;
                }
                currentStream = null;
            }
        }

        private void InitializeNextFilePart()
        {
            maxPosition = filePartEnumerator.Current.FileHeader.CompressedSize;
            currentPosition = 0;
            currentStream = filePartEnumerator.Current.GetCompressedStream();

            currentPartTotalReadBytes = 0;

            CurrentCrc = filePartEnumerator.Current.FileHeader.FileCrc;

            streamListener.FireFilePartExtractionBegin(filePartEnumerator.Current.FilePartName,
                                                       filePartEnumerator.Current.FileHeader.CompressedSize,
                                                       filePartEnumerator.Current.FileHeader.UncompressedSize);
        }

        private int GetFilePart(long offset)
        {
            if (offset > maxOffset)
            {
                return fileParts.Count() - 1;
            }

            RarFilePart currentPart = fileParts.First();
            long currentOffset = currentPart.GetCompressedStream().Length;
            int currentFile = 0;

            while (currentOffset < offset)
            {
                currentPart = fileParts.ElementAt(++currentFile);
                currentOffset += currentPart.FileHeader.CompressedSize;
            }

            return currentFile;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            int currentOffset = offset;
            int currentCount = count;
            while (currentCount > 0)
            {
                int readSize = currentCount;
                if (currentCount > maxPosition - currentPosition)
                {
                    readSize = (int)(maxPosition - currentPosition);
                }

                int read = currentStream.Read(buffer, currentOffset, readSize);
                if (read < 0)
                {
                    throw new EndOfStreamException();
                }

                currentPosition += read;
                currentOffset += read;
                currentCount -= read;
                totalRead += read;
                if (((maxPosition - currentPosition) == 0)
                    && filePartEnumerator.Current.FileHeader.IsSplitAfter)
                {
                    if (filePartEnumerator.Current.FileHeader.R4Salt != null)
                    {
                        throw new InvalidFormatException("Sharpcompress currently does not support multi-volume decryption.");
                    }
                    string fileName = filePartEnumerator.Current.FileHeader.FileName;
                    if (!filePartEnumerator.MoveNext())
                    {
                        throw new InvalidFormatException(
                                                         "Multi-part rar file is incomplete.  Entry expects a new volume: " + fileName);
                    }
                    InitializeNextFilePart();
                }
                else
                {
                    break;
                }
            }
            currentPartTotalReadBytes += totalRead;
            currentEntryTotalReadBytes += totalRead;
            streamListener.FireCompressedBytesRead(currentPartTotalReadBytes, currentEntryTotalReadBytes);
            return totalRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public uint CurrentCrc { get; private set; }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin origin)
        {

            var part = GetFilePart(offset);

            for (int i = 0; i < part; i++)
            {
                filePartEnumerator.MoveNext();
            }

            filePartEnumerator = fileParts.Where((filePart, index) => index >= part).GetEnumerator();
            filePartEnumerator.MoveNext();
            InitializeNextFilePart();

            long currentOffset = fileParts.Where((filePart, index) => index < part)
                .Select(filePart => filePart.FileHeader.CompressedSize)
                .Sum();

            long currentFileOffset = offset - currentOffset;

            currentStream.Seek(currentFileOffset, SeekOrigin.Begin);

            currentPosition = currentFileOffset;

            return currentPosition;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}