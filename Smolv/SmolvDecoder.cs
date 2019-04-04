using System;
using System.IO;

namespace Smolv
{
	public static class SmolvDecoder
	{
		public static int GetDecodedBufferSize(byte[] data)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			if (!CheckSmolHeader(data))
			{
				return 0;
			}

			int size = BitConverter.ToInt32(data, 5 * sizeof(uint));
			return size;
		}

		public static byte[] Decode(byte[] data)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			int bufferSize = GetDecodedBufferSize(data);
			if (bufferSize == 0)
			{
				// invalid SMOL-V
				return null;
			}

			byte[] output = new byte[bufferSize];
			if (Decode(data, output))
			{
				return output;
			}

			return null;
		}

		public static bool Decode(byte[] data, byte[] output)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}
			if (output == null)
			{
				throw new ArgumentNullException(nameof(output));
			}

			int bufferSize = GetDecodedBufferSize(data);
			if (bufferSize > output.Length)
			{
				return false;
			}

			using (MemoryStream stream = new MemoryStream(output))
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					return Decode(data, writer);
				}
			}
		}

		public static bool Decode(byte[] data, BinaryWriter output)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}
			if (output == null)
			{
				throw new ArgumentNullException(nameof(output));
			}

			int bufferSize = GetDecodedBufferSize(data);
			if (bufferSize == 0)
			{
				// invalid SMOL-V
				return false;
			}

			using (MemoryStream stream = new MemoryStream(data))
			{
				using (BinaryReader input = new BinaryReader(stream))
				{
					// Header
					output.Write(SpirVHeaderMagic);
					input.BaseStream.Position += sizeof(uint);
					uint version = input.ReadUInt32();
					output.Write(version);
					uint generator = input.ReadUInt32();
					output.Write(generator);
					int bound = input.ReadInt32();
					output.Write(bound);
					uint schema = input.ReadUInt32();
					output.Write(schema);
					// decode buffer size
					input.ReadInt32();

					// Body
					int prevResult = 0;
					int prevDecorate = 0;
					while (input.BaseStream.Position != input.BaseStream.Length)
					{
						// read length + opcode
						if (!ReadLengthOp(input, out uint instrLen, out SpvOp op))
						{
							return false;
						}

						bool wasSwizzle = op == SpvOp.VectorShuffleCompact;
						if (wasSwizzle)
						{
							op = SpvOp.VectorShuffle;
						}
						output.Write((instrLen << 16) | (uint)op);

						uint ioffs = 1;
						// read type as varint, if we have it
						if (op.OpHasType())
						{
							if (!ReadVarint(input, out uint value))
							{
								return false;
							}

							output.Write(value);
							ioffs++;
						}

						// read result as delta+varint, if we have it
						if (op.OpHasResult())
						{
							if (!ReadVarint(input, out uint value))
							{
								return false;
							}

							int zds = prevResult + ZigDecode(value);
							output.Write(zds);
							prevResult = zds;
							ioffs++;
						}

						// Decorate: IDs relative to previous decorate
						if (op == SpvOp.Decorate || op == SpvOp.MemberDecorate)
						{
							if (!ReadVarint(input, out uint value))
							{
								return false;
							}

							int zds = prevDecorate + (int)value;
							output.Write(zds);
							prevDecorate = zds;
							ioffs++;
						}

						// Read this many IDs, that are relative to result ID
						int relativeCount = op.OpDeltaFromResult();
						bool inverted = false;
						if (relativeCount < 0)
						{
							inverted = true;
							relativeCount = -relativeCount;
						}
						for (int i = 0; i < relativeCount && ioffs < instrLen; ++i, ++ioffs)
						{
							if (!ReadVarint(input, out uint value))
							{
								return false;
							}

							int zd = inverted ? ZigDecode(value) : (int)value;
							output.Write(prevResult - zd);
						}

						if (wasSwizzle && instrLen <= 9)
						{
							uint swizzle = input.ReadByte();
							if (instrLen > 5) output.Write(swizzle >> 6);
							if (instrLen > 6) output.Write((swizzle >> 4) & 3);
							if (instrLen > 7) output.Write((swizzle >> 2) & 3);
							if (instrLen > 8) output.Write(swizzle & 3);
						}
						else if (op.OpVarRest())
						{
							// read rest of words with variable encoding
							for (; ioffs < instrLen; ++ioffs)
							{
								if (!ReadVarint(input, out uint value))
								{
									return false;
								}
								output.Write(value);
							}
						}
						else
						{
							// read rest of words without any encoding
							for (; ioffs < instrLen; ++ioffs)
							{
								if (input.BaseStream.Position + 4 > input.BaseStream.Length)
								{
									return false;
								}
								uint val = input.ReadUInt32();
								output.Write(val);
							}
						}
					}

					if (output.BaseStream.Position != bufferSize)
					{
						// something went wrong during decoding? we should have decoded to exact output size
						return false;
					}

					return true;
				}
			}
		}

		private static bool CheckSmolHeader(byte[] data)
		{
			if (!CheckGenericHeader(data, SmolHeaderMagic))
			{
				return false;
			}
			if (data.Length < 24)
			{
				// one more word past header to store decoded length
				return false;
			}

			return true;
		}

		private static bool CheckGenericHeader(byte[] data, uint expectedMagic)
		{
			if (data == null)
			{
				return false;
			}
			if (data.Length < 5 * sizeof(uint))
			{
				return false;
			}

			uint headerMagic = BitConverter.ToUInt32(data, 0 * sizeof(uint));
			if (headerMagic != expectedMagic)
			{
				return false;
			}

			uint headerVersion = BitConverter.ToUInt32(data, 1 * sizeof(uint));
			if (headerVersion < 0x00010000 || headerVersion > 0x00010300)
			{
				// only support 1.0 through 1.3
				return false;
			}

			return true;
		}

		private static bool ReadVarint(BinaryReader input, out uint value)
		{
			uint v = 0;
			int shift = 0;
			while (input.BaseStream.Position < input.BaseStream.Length)
			{
				byte b = input.ReadByte();
				v |= unchecked((uint)(b & 127) << shift);
				shift += 7;
				if ((b & 128) == 0)
				{
					break;
				}
			}

			value = v;
			// @TODO: report failures
			return true;
		}

		private static bool ReadLengthOp(BinaryReader input, out uint len, out SpvOp op)
		{
			len = default;
			op = default;
			if (!ReadVarint(input, out uint value))
			{
				return false;
			}
			len = ((value >> 20) << 4) | ((value >> 4) & 0xF);
			op = (SpvOp) (((value >> 4) & 0xFFF0) | (value & 0xF));

			op = RemapOp(op);
			len = DecodeLen(op, len);
			return true;
		}

		/// <summary>
		/// Remap most common Op codes (Load, Store, Decorate, VectorShuffle etc.) to be in &lt; 16 range, for 
		/// more compact varint encoding. This basically swaps rarely used op values that are &lt; 16 with the
		/// ones that are common.
		/// </summary>
		private static SpvOp RemapOp(SpvOp op)
		{
			// 0: 24%
			if (op == SpvOp.Decorate) return SpvOp.Nop;
			if (op == SpvOp.Nop) return SpvOp.Decorate;
			// 1: 17%
			if (op == SpvOp.Load) return SpvOp.Undef;
			if (op == SpvOp.Undef) return SpvOp.Load;
			// 2: 9%
			if (op == SpvOp.Store) return SpvOp.SourceContinued;
			if (op == SpvOp.SourceContinued) return SpvOp.Store;
			// 3: 7.2%
			if (op == SpvOp.AccessChain) return SpvOp.Source;
			if (op == SpvOp.Source) return SpvOp.AccessChain;
			// 4: 5.0%
			// Name - already small enum value - 5: 4.4%
			// MemberName - already small enum value - 6: 2.9% 
			if (op == SpvOp.VectorShuffle) return SpvOp.SourceExtension;
			if (op == SpvOp.SourceExtension) return SpvOp.VectorShuffle;
			// 7: 4.0%
			if (op == SpvOp.MemberDecorate) return SpvOp.String;
			if (op == SpvOp.String) return SpvOp.MemberDecorate;
			// 8: 0.9%
			if (op == SpvOp.Label) return SpvOp.Line;
			if (op == SpvOp.Line) return SpvOp.Label;
			// 9: 3.9%
			if (op == SpvOp.Variable) return (SpvOp)9;
			if (op == (SpvOp)9) return SpvOp.Variable;
			// 10: 3.9%
			if (op == SpvOp.FMul) return SpvOp.Extension;
			if (op == SpvOp.Extension) return SpvOp.FMul;
			// 11: 2.5%
			// ExtInst - already small enum value - 12: 1.2%
			// VectorShuffleCompact - already small enum value - used for compact shuffle encoding
			if (op == SpvOp.FAdd) return SpvOp.ExtInstImport;
			if (op == SpvOp.ExtInstImport) return SpvOp.FAdd;
			// 14: 2.2%
			if (op == SpvOp.TypePointer) return SpvOp.MemoryModel;
			if (op == SpvOp.MemoryModel) return SpvOp.TypePointer;
			// 15: 1.1%
			if (op == SpvOp.FNegate) return SpvOp.EntryPoint;
			if (op == SpvOp.EntryPoint) return SpvOp.FNegate;
			return op;
		}

		private static uint DecodeLen(SpvOp op, uint len)
		{
			len++;
			if (op == SpvOp.VectorShuffle) len += 4;
			if (op == SpvOp.VectorShuffleCompact) len += 4;
			if (op == SpvOp.Decorate) len += 2;
			if (op == SpvOp.Load) len += 3;
			if (op == SpvOp.AccessChain) len += 3;
			return len;
		}

		private static int DecorationExtraOps(int dec)
		{
			// RelaxedPrecision, Block..ColMajor
			if (dec == 0 || (dec >= 2 && dec <= 5))
			{
				return 0;
			}
			// Stream..XfbStride
			if (dec >= 29 && dec <= 37)
			{
				return 1;
			}

			// unknown, encode length
			return -1;
		}

		private static int ZigDecode(uint u)
		{
			return (u & 1) != 0 ? unchecked((int)(~(u >> 1))) : unchecked((int)(u >> 1));
		}

		public const uint SpirVHeaderMagic = 0x07230203;
		/// <summary>
		/// 'SMOL' ascii
		/// </summary>
		public const uint SmolHeaderMagic = 0x534D4F4C;
	}
}
