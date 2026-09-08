using System;
using System.Reflection;
using OSGeo.OSR;
class Program {
    static void Main() {
        var methods = typeof(CoordinateTransformation).GetMethods();
        foreach (var m in methods) {
            if (m.Name == ""TransformPoint"") {
                Console.WriteLine(m.ToString());
            }
        }
    }
}
