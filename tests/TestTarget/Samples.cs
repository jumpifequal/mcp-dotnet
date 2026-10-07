using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// Deterministic fixture for the mcp-dotnet test-suite. The code is analysed and patched, never run by the server.
namespace TestTarget
{
    public interface IShape
    {
        double Area();
    }

    public abstract class ShapeBase : IShape
    {
        public abstract double Area();
    }

    public sealed class Square : ShapeBase
    {
        private readonly double _side;

        public Square(double side) => _side = side;

        public override double Area() => _side * _side;
    }

    public sealed class Circle : ShapeBase
    {
        public double Radius { get; set; }

        public event EventHandler Changed;

        public override double Area() => Math.PI * Radius * Radius;

        public void Touch() => Changed?.Invoke(this, EventArgs.Empty);
    }

    public static class Calculator
    {
        public const string Banner = "calc-v1";

        public static readonly byte[] Key = { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0, 0xC0 };

        public static int Counter = 7;

        public static int Answer() => 41;

        public static int Add(int a, int b) => a + b;

        public static bool CheckLicense(string key)
        {
            if (key == null)
                return false;
            if (key.Length != 16)
                return false;
            return key.StartsWith("PRO-", StringComparison.Ordinal);
        }

        public static string Classify(int value)
        {
            switch (value)
            {
                case 0: return "zero";
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                default: return value < 0 ? "negative" : "many";
            }
        }

        public static int SafeDivide(int a, int b)
        {
            try
            {
                return a / b;
            }
            catch (DivideByZeroException)
            {
                return 0;
            }
        }

        public static string Secret() => "secret-api-key-123";

        public static string Url() => "https://example.invalid/c2/beacon";

        public class Inner
        {
            public static int Depth() => 2;
        }
    }

    public static class Suspicious
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

        public static IntPtr Allocate() => VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);

        public static void Launch() => Process.Start("cmd.exe", "/c whoami");

        public static Assembly LoadPayload(string b64) => Assembly.Load(Convert.FromBase64String(b64));

        public static bool Debugged() => Debugger.IsAttached;
    }
}
