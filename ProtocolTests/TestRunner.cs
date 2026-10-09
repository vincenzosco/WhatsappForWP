using System;
using System.Collections.Generic;
using System.Reflection;

namespace ProtocolTests
{
    /// <summary>
    /// Runs every <see cref="TestAttribute"/> method in this assembly and exits
    /// non-zero if one failed.
    ///
    /// It is deliberately not a test framework: the protocol core is compiled
    /// inside the WP8.1 app, which has no test host, so the only place the code is
    /// exercised outside the phone is here. A dependency-free runner keeps that
    /// possible, and one line per test keeps the output readable in a terminal.
    /// </summary>
    public static class TestRunner
    {
        public static int Main(string[] args)
        {
            var failures = new List<string>();
            var tests = new List<MethodInfo>();

            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                // A static class is abstract sealed, so `IsAbstract` cannot be
                // used to mean "not a test holder": the tests live in static
                // classes and the old filter skipped every one of them.
                if (!type.IsClass) continue;

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.GetCustomAttributes(typeof(TestAttribute), false).Length == 0) continue;
                    if (method.GetParameters().Length != 0) continue;

                    tests.Add(method);
                }
            }

            // Deterministic order, so two runs of the same failure read the same.
            tests.Sort(delegate (MethodInfo left, MethodInfo right)
            {
                int byType = string.CompareOrdinal(left.DeclaringType.FullName, right.DeclaringType.FullName);
                if (byType != 0) return byType;
                return string.CompareOrdinal(left.Name, right.Name);
            });

            int passed = 0;
            foreach (MethodInfo test in tests)
            {
                string name = test.DeclaringType.Name + "." + test.Name;

                try
                {
                    test.Invoke(null, null);
                    passed++;
                    Console.WriteLine("  pass  " + name);
                }
                catch (TargetInvocationException wrapped)
                {
                    Exception cause = wrapped.InnerException == null ? wrapped : wrapped.InnerException;

                    failures.Add(name + ": " + cause.GetType().Name + ": " + cause.Message);
                    Console.WriteLine("  FAIL  " + name);
                    Console.WriteLine("        " + cause.GetType().Name + ": "
                        + cause.Message.Replace("\n", "\n        "));
                }
            }

            Console.WriteLine();
            if (failures.Count > 0)
            {
                Console.WriteLine("FAILED: " + failures.Count + " of " + tests.Count + " protocol test(s).");
                return 1;
            }

            Console.WriteLine("OK: " + passed + " protocol test(s)");
            return 0;
        }
    }
}
