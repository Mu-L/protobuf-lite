#region Copyright notice and license
// Protocol Buffers - Google's data interchange format
// Copyright 2008 Google Inc.  All rights reserved.
//
// Use of this source code is governed by a BSD-style
// license that can be found in the LICENSE file or at
// https://developers.google.com/open-source/licenses/bsd
#endregion

using Google.Protobuf.Collections;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace Google.Protobuf
{
    /// <summary>
    /// Reads and decodes protocol message fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is generally used by generated code to read appropriate
    /// primitives from the stream. It effectively encapsulates the lowest
    /// levels of protocol buffer format.
    /// </para>
    /// <para>
    /// Repeated fields and map fields are not handled by this class; use <see cref="RepeatedField{T}"/>
    /// and <see cref="MapField{TKey, TValue}"/> to serialize such fields.
    /// </para>
    /// </remarks>
    [SecuritySafeCritical]
    public sealed class CodedInputStream : IDisposable
    {
        /// <summary>
        /// Whether to leave the underlying stream open when disposing of this stream.
        /// This is always true when there's no stream.
        /// </summary>
        private readonly bool leaveOpen;

        /// <summary>
        /// Buffer of data read from the stream or provided at construction time.
        /// </summary>
        private readonly byte[] bufferArr;

        private readonly ReadOnlyMemory<byte> buffer;
        /// <summary>
        /// The stream to read further input from, or null if the byte array buffer was provided
        /// directly on construction, with no further data available.
        /// </summary>
        private readonly Stream input;

        /// <summary>
        /// The parser state is kept separately so that other parse implementations can reuse the same
        /// parsing primitives.
        /// </summary>
        private ParserInternalState state;
        internal const int DefaultRecursionLimit = 100;
        internal const int DefaultSizeLimit = Int32.MaxValue;
        internal const int BufferSize = 4096;

        #region Construction
        // Note that the checks are performed such that we don't end up checking obviously-valid things
        // like non-null references for arrays we've just created.

        /// <summary>
        /// Creates a new CodedInputStream reading data from the given byte array.
        /// </summary>
        public CodedInputStream(byte[] buffer) : this(null, ProtoPreconditions.CheckNotNull(buffer, "buffer"), 0, buffer.Length, true)
        {            
        }

        /// <summary>
        /// Creates a new CodedInputStream reading data from the given byte array.
        /// </summary>
        /// <param name="buffer"></param>
        public CodedInputStream(ReadOnlyMemory<byte> buffer)
        {
            this.input = null;
            this.bufferArr = null;
            this.buffer = buffer;
            this.state.bufferPos = 0;
            this.state.bufferSize = buffer.Length;
            this.state.sizeLimit = DefaultSizeLimit;
            this.state.recursionLimit = DefaultRecursionLimit;
            SegmentedBufferHelper.Initialize(this, out this.state.segmentedBufferHelper);
            this.leaveOpen = true;

            this.state.currentLimit = int.MaxValue;
        }

        /// <summary>
        /// Creates a new <see cref="CodedInputStream"/> that reads from the given byte array slice.
        /// </summary>
        public CodedInputStream(byte[] buffer, int offset, int length)
            : this(null, ProtoPreconditions.CheckNotNull(buffer, "buffer"), offset, offset + length, true)
        {            
            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("offset", "Offset must be within the buffer");
            }
            if (length < 0 || offset + length > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("length", "Length must be non-negative and within the buffer");
            }
        }

        /// <summary>
        /// Creates a new <see cref="CodedInputStream"/> reading data from the given stream, which will be disposed
        /// when the returned object is disposed.
        /// </summary>
        /// <param name="input">The stream to read from.</param>
        public CodedInputStream(Stream input) : this(input, false)
        {
        }

        /// <summary>
        /// Creates a new <see cref="CodedInputStream"/> reading data from the given stream.
        /// </summary>
        /// <param name="input">The stream to read from.</param>
        /// <param name="leaveOpen"><c>true</c> to leave <paramref name="input"/> open when the returned
        /// <c cref="CodedInputStream"/> is disposed; <c>false</c> to dispose of the given stream when the
        /// returned object is disposed.</param>
        public CodedInputStream(Stream input, bool leaveOpen)
            : this(ProtoPreconditions.CheckNotNull(input, "input"), new byte[BufferSize], 0, 0, leaveOpen)
        {
        }
        
        /// <summary>
        /// Creates a new CodedInputStream reading data from the given
        /// stream and buffer, using the default limits.
        /// </summary>
        internal CodedInputStream(Stream input, byte[] buffer, int bufferPos, int bufferSize, bool leaveOpen)
        {
            this.input = input;
            this.bufferArr = buffer;
            this.buffer = this.bufferArr;
            this.state.bufferPos = bufferPos;
            this.state.bufferSize = bufferSize;
            this.state.sizeLimit = DefaultSizeLimit;
            this.state.recursionLimit = DefaultRecursionLimit;
            SegmentedBufferHelper.Initialize(this, out this.state.segmentedBufferHelper);
            this.leaveOpen = leaveOpen;

            this.state.currentLimit = int.MaxValue;
        }

        /// <summary>
        /// Creates a new CodedInputStream reading data from the given
        /// stream and buffer, using the specified limits.
        /// </summary>
        /// <remarks>
        /// This chains to the version with the default limits instead of vice versa to avoid
        /// having to check that the default values are valid every time.
        /// </remarks>
        internal CodedInputStream(Stream input, byte[] buffer, int bufferPos, int bufferSize, int sizeLimit, int recursionLimit, bool leaveOpen)
            : this(input, buffer, bufferPos, bufferSize, leaveOpen)
        {
            if (sizeLimit <= 0)
            {
                throw new ArgumentOutOfRangeException("sizeLimit", "Size limit must be positive");
            }
            if (recursionLimit <= 0)
            {
                throw new ArgumentOutOfRangeException("recursionLimit!", "Recursion limit must be positive");
            }
            this.state.sizeLimit = sizeLimit;
            this.state.recursionLimit = recursionLimit;
        }
        #endregion

        /// <summary>
        /// Creates a <see cref="CodedInputStream"/> with the specified size and recursion limits, reading
        /// from an input stream.
        /// </summary>
        /// <remarks>
        /// This method exists separately from the constructor to reduce the number of constructor overloads.
        /// It is likely to be used considerably less frequently than the constructors, as the default limits
        /// are suitable for most use cases.
        /// </remarks>
        /// <param name="input">The input stream to read from</param>
        /// <param name="sizeLimit">The total limit of data to read from the stream.</param>
        /// <param name="recursionLimit">The maximum recursion depth to allow while reading.</param>
        /// <returns>A <c>CodedInputStream</c> reading from <paramref name="input"/> with the specified size
        /// and recursion limits.</returns>
        public static CodedInputStream CreateWithLimits(Stream input, int sizeLimit, int recursionLimit)
        {
            // Note: we may want an overload accepting leaveOpen
            return new CodedInputStream(input, new byte[BufferSize], 0, 0, sizeLimit, recursionLimit, false);
        }

        /// <summary>
        /// Returns the current position in the input stream, or the position in the input buffer
        /// </summary>
        public long Position 
        {
            get
            {
                if (input != null)
                {
                    return input.Position - ((state.bufferSize + state.bufferSizeAfterLimit) - state.bufferPos);
                }
                return state.bufferPos;
            }
        }

        /// <summary>
        /// Returns the last tag read, or 0 if no tags have been read or we've read beyond
        /// the end of the stream.
        /// </summary>
        internal uint LastTag { get { return state.lastTag; } }

        /// <summary>
        /// Returns the size limit for this stream.
        /// </summary>
        /// <remarks>
        /// This limit is applied when reading from the underlying stream, as a sanity check. It is
        /// not applied when reading from a byte array data source without an underlying stream.
        /// The default value is Int32.MaxValue.
        /// </remarks>
        /// <value>
        /// The size limit.
        /// </value>
        public int SizeLimit { get { return state.sizeLimit; } }

        /// <summary>
        /// Returns the recursion limit for this stream. This limit is applied whilst reading messages,
        /// to avoid maliciously-recursive data.
        /// </summary>
        /// <remarks>
        /// The default limit is 100.
        /// </remarks>
        /// <value>
        /// The recursion limit for this stream.
        /// </value>
        public int RecursionLimit { get { return state.recursionLimit; } }

        /// <summary>
        /// Internal-only property; when set to true, unknown fields will be discarded while parsing.
        /// </summary>
        internal bool DiscardUnknownFields
        {
            get { return state.DiscardUnknownFields; }
            set { state.DiscardUnknownFields = value; }
        }

        /*
        /// <summary>
        /// Internal-only property; provides extension identifiers to compatible messages while parsing.
        /// </summary>
        internal ExtensionRegistry ExtensionRegistry
        {
            get { return state.ExtensionRegistry; }
            set { state.ExtensionRegistry = value; }
        }
        */
        internal ReadOnlyMemory<byte> InternalBufferMemory => buffer;

        internal byte[] InternalBuffer => bufferArr;

        internal Stream InternalInputStream => input;

        internal ref ParserInternalState InternalState => ref state;

        /// <summary>
        /// Disposes of this instance, potentially closing any underlying stream.
        /// </summary>
        /// <remarks>
        /// As there is no flushing to perform here, disposing of a <see cref="CodedInputStream"/> which
        /// was constructed with the <c>leaveOpen</c> option parameter set to <c>true</c> (or one which
        /// was constructed to read from a byte array) has no effect.
        /// </remarks>
        public void Dispose()
        {
            if (!leaveOpen)
            {
                input.Dispose();
            }
        }

        #region Validation
        /// <summary>
        /// Verifies that the last call to ReadTag() returned tag 0 - in other words,
        /// we've reached the end of the stream when we expected to.
        /// </summary>
        /// <exception cref="InvalidProtocolBufferException">The 
        /// tag read was not the one specified</exception>
        internal void CheckReadEndOfStreamTag()
        {
            ParsingPrimitivesMessages.CheckReadEndOfStreamTag(ref state);
        }
        #endregion

        #region Reading of tags etc

        /// <summary>
        /// Peeks at the next field tag. This is like calling <see cref="ReadTag"/>, but the
        /// tag is not consumed. (So a subsequent call to <see cref="ReadTag"/> will return the
        /// same value.)
        /// </summary>
        public uint PeekTag()
        {
            var span = buffer.Span;
            return ParsingPrimitives.PeekTag(ref span, ref state);
        }

        /// <summary>
        /// Reads a field tag, returning the tag of 0 for "end of stream".
        /// </summary>
        /// <remarks>
        /// If this method returns 0, it doesn't necessarily mean the end of all
        /// the data in this CodedInputStream; it may be the end of the logical stream
        /// for an embedded message, for example.
        /// </remarks>
        /// <returns>The next field tag, or 0 for end of stream. (0 is never a valid tag.)</returns>
        public uint ReadTag()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseTag(ref span, ref state);
        }

        /// <summary>
        /// Skips the data for the field with the tag we've just read.
        /// This should be called directly after <see cref="ReadTag"/>, when
        /// the caller wishes to skip an unknown field.
        /// </summary>
        /// <remarks>
        /// This method throws <see cref="InvalidProtocolBufferException"/> if the last-read tag was an end-group tag.
        /// If a caller wishes to skip a group, they should skip the whole group, by calling this method after reading the
        /// start-group tag. This behavior allows callers to call this method on any field they don't understand, correctly
        /// resulting in an error if an end-group tag has not been paired with an earlier start-group tag.
        /// </remarks>
        /// <exception cref="InvalidProtocolBufferException">The last tag was an end-group tag</exception>
        /// <exception cref="InvalidOperationException">The last read operation read to the end of the logical stream</exception>
        public void SkipLastField()
        {
            var span = buffer.Span;
            ParsingPrimitivesMessages.SkipLastField(ref span, ref state);
        }

        /// <summary>
        /// Skip a group.
        /// </summary>
        internal void SkipGroup(uint startGroupTag)
        {
            var span = buffer.Span;
            ParsingPrimitivesMessages.SkipGroup(ref span, ref state, startGroupTag);
        }

        /// <summary>
        /// Reads a double field from the stream.
        /// </summary>
        public double ReadDouble()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseDouble(ref span, ref state);
        }

        /// <summary>
        /// Reads a float field from the stream.
        /// </summary>
        public float ReadFloat()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseFloat(ref span, ref state);
        }

        /// <summary>
        /// Reads a uint64 field from the stream.
        /// </summary>
        public ulong ReadUInt64()
        {
            return ReadRawVarint64();
        }

        /// <summary>
        /// Reads an int64 field from the stream.
        /// </summary>
        public long ReadInt64()
        {
            return (long) ReadRawVarint64();
        }

        /// <summary>
        /// Reads an int32 field from the stream.
        /// </summary>
        public int ReadInt32()
        {
            return (int) ReadRawVarint32();
        }

        /// <summary>
        /// Reads a fixed64 field from the stream.
        /// </summary>
        public ulong ReadFixed64()
        {
            return ReadRawLittleEndian64();
        }

        /// <summary>
        /// Reads a fixed32 field from the stream.
        /// </summary>
        public uint ReadFixed32()
        {
            return ReadRawLittleEndian32();
        }

        /// <summary>
        /// Reads a bool field from the stream.
        /// </summary>
        public bool ReadBool()
        {
            return ReadRawVarint64() != 0;
        }

        /// <summary>
        /// Reads a string field from the stream.
        /// </summary>
        public string ReadString()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ReadString(ref span, ref state);
        }
        /*
        /// <summary>
        /// Reads an embedded message field value from the stream.
        /// </summary>
        public void ReadMessage(IMessage builder)
        {
            // TODO: if the message doesn't implement IBufferMessage (and thus does not provide the InternalMergeFrom method),
            // what we're doing here works fine, but could be more efficient.
            // What happens is that we first initialize a ParseContext from the current coded input stream only to parse the length of the message, at which point
            // we will need to switch back again to CodedInputStream-based parsing (which involves copying and storing the state) to be able to
            // invoke the legacy MergeFrom(CodedInputStream) method.
            // For now, this inefficiency is fine, considering this is only a backward-compatibility scenario (and regenerating the code fixes it).
            ParseContext.Initialize(buffer.AsSpan(), ref state, out ParseContext ctx);
            try
            {
                ParsingPrimitivesMessages.ReadMessage(ref ctx, builder);
            }
            finally
            {
                ctx.CopyStateTo(this);
            }
        }

        /// <summary>
        /// Reads an embedded group field from the stream.
        /// </summary>
        public void ReadGroup(IMessage builder)
        {
            ParseContext.Initialize(this, out ParseContext ctx);
            try
            {
                ParsingPrimitivesMessages.ReadGroup(ref ctx, builder);
            }
            finally
            {
                ctx.CopyStateTo(this);
            }
        }
        */
        /// <summary>
        /// Reads a bytes field value from the stream.
        /// </summary>   
        public ByteString ReadBytes()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ReadBytes(ref span, ref state);
        }

        /// <summary>
        /// Reads a uint32 field value from the stream.
        /// </summary>   
        public uint ReadUInt32()
        {
            return ReadRawVarint32();
        }

        /// <summary>
        /// Reads an enum field value from the stream.
        /// </summary>   
        public int ReadEnum()
        {
            // Currently just a pass-through, but it's nice to separate it logically from WriteInt32.
            return (int) ReadRawVarint32();
        }

        /// <summary>
        /// Reads an sfixed32 field value from the stream.
        /// </summary>   
        public int ReadSFixed32()
        {
            return (int) ReadRawLittleEndian32();
        }

        /// <summary>
        /// Reads an sfixed64 field value from the stream.
        /// </summary>   
        public long ReadSFixed64()
        {
            return (long) ReadRawLittleEndian64();
        }

        /// <summary>
        /// Reads an sint32 field value from the stream.
        /// </summary>   
        public int ReadSInt32()
        {
            return ParsingPrimitives.DecodeZigZag32(ReadRawVarint32());
        }

        /// <summary>
        /// Reads an sint64 field value from the stream.
        /// </summary>   
        public long ReadSInt64()
        {
            return ParsingPrimitives.DecodeZigZag64(ReadRawVarint64());
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadStringArray(uint fieldTag, ICollection<string> list)
        {
            string tmp = null;
            do
            {
                tmp = ReadString();
                list.Add(tmp);
            } while (ContinueArray(fieldTag));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadBytesArray(uint fieldTag, ICollection<ByteString> list)
        {
            ByteString tmp = null;
            do
            {
                tmp = ReadBytes();
                list.Add(tmp);
            } while (ContinueArray(fieldTag));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadBoolArray(uint fieldTag, ICollection<bool> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                bool tmp = false;
                do
                {
                    tmp = ReadBool();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadInt32Array(uint fieldTag, ICollection<int> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                int tmp = 0;
                do
                {
                    tmp = ReadInt32();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadSInt32Array(uint fieldTag, ICollection<int> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                int tmp = 0;
                do
                {
                    tmp = ReadSInt32();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadUInt32Array(uint fieldTag, ICollection<uint> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                uint tmp = 0;
                do
                {
                    tmp = ReadUInt32();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadFixed32Array(uint fieldTag, ICollection<uint> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                uint tmp = 0;
                do
                {
                    tmp = ReadFixed32();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadSFixed32Array(uint fieldTag, ICollection<int> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                int tmp = 0;
                do
                {
                    tmp = ReadSFixed32();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadInt64Array(uint fieldTag, ICollection<long> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                long tmp = 0;
                do
                {
                    tmp = ReadInt64();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadSInt64Array(uint fieldTag, ICollection<long> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                long tmp = 0;
                do
                {
                    tmp = ReadSInt64();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadUInt64Array(uint fieldTag, ICollection<ulong> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                ulong tmp = 0;
                do
                {
                    tmp = ReadUInt64();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadFixed64Array(uint fieldTag, ICollection<ulong> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                ulong tmp = 0;
                do
                {
                    tmp = ReadFixed64();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadSFixed64Array(uint fieldTag, ICollection<long> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                long tmp = 0;
                do
                {
                    tmp = ReadSFixed64();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadDoubleArray(uint fieldTag, ICollection<double> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                double tmp = 0;
                do
                {
                    tmp = ReadDouble();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadFloatArray(uint fieldTag, ICollection<float> list)
        {
            bool isPacked;
            int holdLimit;
            if (BeginArray(fieldTag, out isPacked, out holdLimit))
            {
                float tmp = 0;
                do
                {
                    tmp = ReadFloat();
                    list.Add(tmp);
                } while (ContinueArray(fieldTag, isPacked, holdLimit));
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="fieldTag"></param>
        /// <param name="list"></param>
        public void ReadEnumArray(uint fieldTag, ICollection<int> list)
        {
            WireFormat.WireType wformat = WireFormat.GetTagWireType(fieldTag);

            // 2.3 allows packed form even if the field is not declared packed.
            if (wformat == WireFormat.WireType.LengthDelimited)
            {
                int length = (int)(ReadRawVarint32() & int.MaxValue);
                int limit = PushLimit(length);
                while (!ReachedLimit)
                {
                    list.Add(ReadEnum());
                }
                PopLimit(limit);
            }
            else
            {
                do
                {
                    list.Add(ReadEnum());
                } while (ContinueArray(fieldTag));
            }
        }

        /// <summary>
        /// Reads a length for length-delimited data.
        /// </summary>
        /// <remarks>
        /// This is internally just reading a varint, but this method exists
        /// to make the calling code clearer.
        /// </remarks>
        public int ReadLength()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseLength(ref span, ref state);
        }

        /// <summary>
        /// Peeks at the next tag in the stream. If it matches <paramref name="tag"/>,
        /// the tag is consumed and the method returns <c>true</c>; otherwise, the
        /// stream is left in the original position and the method returns <c>false</c>.
        /// </summary>
        public bool MaybeConsumeTag(uint tag)
        {
            var span = buffer.Span;
            return ParsingPrimitives.MaybeConsumeTag(ref span, ref state, tag);
        }

        private bool BeginArray(uint fieldTag, out bool isPacked, out int oldLimit)
        {
            isPacked = WireFormat.GetTagWireType(fieldTag) == WireFormat.WireType.LengthDelimited;

            if (isPacked)
            {
                int length = (int)(ReadRawVarint32() & int.MaxValue);
                if (length > 0)
                {
                    oldLimit = PushLimit(length);
                    return true;
                }
                oldLimit = -1;
                return false; //packed but empty
            }

            oldLimit = -1;
            return true;
        }
        /// <summary>
        /// Returns true if the next tag is also part of the same unpacked array.
        /// </summary>
        private bool ContinueArray(uint currentTag)
        {
            uint next = PeekTag();
            if (next != 0)
            {
                if (next == currentTag)
                {
                    state.hasNextTag = false;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if the next tag is also part of the same array, which may or may not be packed.
        /// </summary>
        private bool ContinueArray(uint currentTag, bool packed, int oldLimit)
        {
            if (packed)
            {
                if (ReachedLimit)
                {
                    PopLimit(oldLimit);
                    return false;
                }
                return true;
            }

            uint next = PeekTag();
            if (next != 0)
            {
                if (next == currentTag)
                {
                    state.hasNextTag = false;
                    return true;
                }
            }
            return false;
        }
        #endregion

        #region Underlying reading primitives

        /// <summary>
        /// Reads a raw Varint from the stream.  If larger than 32 bits, discard the upper bits.
        /// This method is optimised for the case where we've got lots of data in the buffer.
        /// That means we can check the size just once, then just read directly from the buffer
        /// without constant rechecking of the buffer length.
        /// </summary>
        internal uint ReadRawVarint32()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseRawVarint32(ref span, ref state);
        }

        /// <summary>
        /// Reads a varint from the input one byte at a time, so that it does not
        /// read any bytes after the end of the varint. If you simply wrapped the
        /// stream in a CodedInputStream and used ReadRawVarint32(Stream)
        /// then you would probably end up reading past the end of the varint since
        /// CodedInputStream buffers its input.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        internal static uint ReadRawVarint32(Stream input)
        {
            return ParsingPrimitives.ReadRawVarint32(input);
        }

        /// <summary>
        /// Reads a raw varint from the stream.
        /// </summary>
        internal ulong ReadRawVarint64()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseRawVarint64(ref span, ref state);
        }

        /// <summary>
        /// Reads a 32-bit little-endian integer from the stream.
        /// </summary>
        internal uint ReadRawLittleEndian32()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseRawLittleEndian32(ref span, ref state);
        }

        /// <summary>
        /// Reads a 64-bit little-endian integer from the stream.
        /// </summary>
        internal ulong ReadRawLittleEndian64()
        {
            var span = buffer.Span;
            return ParsingPrimitives.ParseRawLittleEndian64(ref span, ref state);
        }
        #endregion

        #region Internal reading and buffer management

        /// <summary>
        /// Sets currentLimit to (current position) + byteLimit. This is called
        /// when descending into a length-delimited embedded message. The previous
        /// limit is returned.
        /// </summary>
        /// <returns>The old limit.</returns>
        public int PushLimit(int byteLimit)
        {
            return SegmentedBufferHelper.PushLimit(ref state, byteLimit);
        }

        /// <summary>
        /// Discards the current limit, returning the previous limit.
        /// </summary>
        public void PopLimit(int oldLimit)
        {
            SegmentedBufferHelper.PopLimit(ref state, oldLimit);
        }

        /// <summary>
        /// Returns whether or not all the data before the limit has been read.
        /// </summary>
        /// <returns></returns>
        internal bool ReachedLimit
        {
            get
            {
                return SegmentedBufferHelper.IsReachedLimit(ref state);
            }
        }

        /// <summary>
        /// Returns true if the stream has reached the end of the input. This is the
        /// case if either the end of the underlying input source has been reached or
        /// the stream has reached a limit created using PushLimit.
        /// </summary>
        public bool IsAtEnd
        {
            get
            {
                var span = buffer.Span;
                return SegmentedBufferHelper.IsAtEnd(ref span, ref state);
            }
        }

        /// <summary>
        /// Reads a fixed size of bytes from the input.
        /// </summary>
        /// <exception cref="InvalidProtocolBufferException">
        /// the end of the stream or the current limit was reached
        /// </exception>
        internal byte[] ReadRawBytes(int size)
        {
            var span = buffer.Span;
            return ParsingPrimitives.ReadRawBytes(ref span, ref state, size);
        }
        /*
        /// <summary>
        /// Reads a top-level message or a nested message after the limits for this message have been pushed.
        /// (parser will proceed until the end of the current limit)
        /// NOTE: this method needs to be public because it's invoked by the generated code - e.g. msg.MergeFrom(CodedInputStream input) method
        /// </summary>
        public void ReadRawMessage(IMessage message)
        {
            ParseContext.Initialize(this, out ParseContext ctx);
            try
            {
                ParsingPrimitivesMessages.ReadRawMessage(ref ctx, message);
            }
            finally
            {
                ctx.CopyStateTo(this);
            }
        }*/
#endregion
    }
}
