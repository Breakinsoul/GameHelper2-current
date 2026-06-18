namespace OffsetSearchTool
{
    using System;
    using GameOffsets;

    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: OffsetSearchTool <PathOfExile.exe or binary dump>");
                return 2;
            }

            try
            {
                var results = PatternSearchEngine.FindStaticOffsetsInFile(args[0]);
                foreach (var (name, offset) in results)
                {
                    Console.WriteLine($"{name}: 0x{offset:X}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
