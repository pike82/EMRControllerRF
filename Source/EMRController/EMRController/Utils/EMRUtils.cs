using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Debug = UnityEngine.Debug;

namespace EMRController.Utils
{
	static class EMRUtils
	{
		private const string logName = "EMR";
		private static bool enabled = false;
        private static readonly string DebugLogModName = "EMRController";
        public static void Log(params object[] message)
		{
			Log(Array.ConvertAll(message, item => item.ToString()));
		}

		public static void Log(params string[] message)
		{
			var builder = StringBuilderCache.Acquire();
			builder.Append("[").Append(logName).Append("] ");
			builder.Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")).Append(" - ");
			foreach (string part in message) {
				builder.Append(part);
			}
			if (enabled) { 
				UnityEngine.Debug.Log(builder.ToStringAndRelease());
			}
		}
        public static void MyDebugLog(object message) //Pike change
        {
            Debug.Log(DebugLogModName + ": " + " " + message);
        }
    }
}
