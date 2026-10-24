using System;
using NGGMultiplayer.Core.Diagnostics;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>Raises events so a throwing subscriber is logged instead of unwinding packet dispatch.</summary>
    internal static class SafeInvoke
    {
        public static void Raise(Action handler, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                LogFailure(name, e);
            }
        }

        public static void Raise<T>(Action<T> handler, T arg, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg);
            }
            catch (Exception e)
            {
                LogFailure(name, e);
            }
        }

        public static void Raise<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg1, arg2);
            }
            catch (Exception e)
            {
                LogFailure(name, e);
            }
        }

        public static void Raise<T1, T2, T3>(Action<T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3, string name)
        {
            if (handler == null)
                return;
            try
            {
                handler(arg1, arg2, arg3);
            }
            catch (Exception e)
            {
                LogFailure(name, e);
            }
        }

        private static void LogFailure(string name, Exception e) =>
            NetLog.Error($"[SaveSync] {name} handler threw: {e}");
    }
}
