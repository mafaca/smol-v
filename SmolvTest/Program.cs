using Smolv;
using System;
using System.IO;

namespace SmolvTest
{
	public static class Program
	{
		public static void Main(string[] args)
		{
			if (args.Length == 0)
			{
				Console.WriteLine("No argument. Provide a path to smolv shader");
			}
			else if (!File.Exists(args[0]))
			{
				Console.WriteLine($"File {args[0]} doesn't exists");
			}
			else
			{
				byte[] data = null;
				string filePath = args[0];
				using (FileStream fs = File.OpenRead(filePath))
				{
					data = new byte[fs.Length];
					fs.Read(data, 0, data.Length);
				}

				byte[] decoded = SmolvDecoder.Decode(data);
				if (decoded == null)
				{
					Console.WriteLine("Unable to decode smolv shader");
				}
				else
				{

					string dirPath = Path.GetDirectoryName(filePath);
					string fileName = Path.GetFileNameWithoutExtension(filePath);
					string fileExtension = Path.GetExtension(filePath);
					string newFilePath = Path.Combine(dirPath, fileName + "_unpacked" + fileExtension);
					using (FileStream fs = File.Create(newFilePath))
					{
						fs.Write(decoded, 0, decoded.Length);
					}
					Console.WriteLine("Finished");
				}
			}

			Console.ReadKey();
		}
	}
}
