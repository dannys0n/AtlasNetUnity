using AtlasNet;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AtlasNetDemo.Tests
{
    public sealed class NetworkLoggingTests
    {
        [TestCase(NetworkLogLevel.Full, true, true, true)]
        [TestCase(NetworkLogLevel.Warnings, false, true, true)]
        [TestCase(NetworkLogLevel.Errors, false, false, true)]
        [TestCase(NetworkLogLevel.None, false, false, false)]
        public void LogLevelsFilterAtlasNetWithoutMutingOtherUnityMessages(NetworkLogLevel level, bool info, bool warnings, bool errors)
        {
            var go = new GameObject("Log filtering test");
            bool unityLogging = Debug.unityLogger.logEnabled;
            try
            {
                var manager = go.AddComponent<NetworkManager>();
                Assert.AreEqual(NetworkLogLevel.Warnings, manager.LogLevel);
                manager.LogLevel = level;
                Assert.AreEqual(info, manager.ShouldLog(LogType.Log));
                Assert.AreEqual(warnings, manager.ShouldLog(LogType.Warning));
                Assert.AreEqual(errors, manager.ShouldLog(LogType.Error));
                Assert.AreEqual(errors, manager.ShouldLog(LogType.Exception));
                Assert.AreEqual(errors, manager.ShouldLog(LogType.Assert));
                if (info) LogAssert.Expect(LogType.Log, "Atlas info probe");
                manager.Log(LogType.Log, "Atlas info probe");
                if (warnings) LogAssert.Expect(LogType.Warning, "Atlas warning probe");
                manager.Log(LogType.Warning, "Atlas warning probe");
                if (errors) LogAssert.Expect(LogType.Error, "Atlas error probe");
                manager.Log(LogType.Error, "Atlas error probe");
                Assert.AreEqual(unityLogging, Debug.unityLogger.logEnabled);
                LogAssert.Expect(LogType.Warning, "Unrelated Unity warning");
                Debug.LogWarning("Unrelated Unity warning");
                LogAssert.NoUnexpectedReceived();
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void NoneSuppressesMessagingDiagnosticsAndLevelsArePerManager()
        {
            var a = new GameObject("Silent manager").AddComponent<NetworkManager>();
            var b = new GameObject("Error manager").AddComponent<NetworkManager>();
            try
            {
                a.LogLevel = NetworkLogLevel.None;
                b.LogLevel = NetworkLogLevel.Errors;
                Assert.DoesNotThrow(() => a.CustomMessagingManager.RegisterNamedMessageHandler(null, (_, _) => { }));
                Assert.DoesNotThrow(() => a.CustomMessagingManager.UnregisterNamedMessageHandler(null));
                Assert.IsFalse(a.ShouldLog(LogType.Error));
                Assert.IsTrue(b.ShouldLog(LogType.Error));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => a.LogLevel = (NetworkLogLevel)99);
                LogAssert.NoUnexpectedReceived();
            }
            finally { Object.DestroyImmediate(a.gameObject); Object.DestroyImmediate(b.gameObject); }
        }
    }
}
