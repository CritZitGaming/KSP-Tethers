using System;
using UnityEngine;

namespace KSPTethers
{
    internal static class TetherLog
    {
        private const string Prefix = "[KSPTethers] ";

        public static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }

        public static void Exception(string context, Exception e)
        {
            Debug.LogError(Prefix + context + ": " + e);
        }
    }
}
