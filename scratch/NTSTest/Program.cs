using System; using NetTopologySuite.Geometries; class P { static void Main() { var env = new Envelope(10, 20, 100, 200); Console.WriteLine(env.Width + " " + env.Height); } }
