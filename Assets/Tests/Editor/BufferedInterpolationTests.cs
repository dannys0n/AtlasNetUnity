using System;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEngine;

namespace AtlasNetDemo.Tests
{
    public sealed class BufferedInterpolationTests
    {
        object buffer;
        Type type;
        void Call(string method, params object[] args) => type.GetMethod(method).Invoke(buffer, args);
        Vector3 At(double time) => (Vector3)type.GetMethod("Evaluate").Invoke(buffer, new object[] { time });
        [SetUp] public void Create()
        {
            type = typeof(NetworkTransform).Assembly.GetType("AtlasNet.BufferedInterpolator`1", true).MakeGenericType(typeof(Vector3));
            buffer = Activator.CreateInstance(type, new object[] { new Func<Vector3, Vector3, float, Vector3>(Vector3.Lerp) });
        }
        [Test] public void MissingUpdateIsBridgedBetweenKnownSnapshots()
        {
            Call("Reset", 0d, Vector3.zero);
            Call("Add", .1d, Vector3.right);
            Call("Add", .3d, Vector3.right * 3);
            Assert.AreEqual(1.5f, At(.15d).x, .0001f);
            Assert.AreEqual(2.5f, At(.25d).x, .0001f);
            Assert.AreEqual(3f, At(1d).x, .0001f, "A long outage must hold, not extrapolate indefinitely.");
        }
        [Test] public void SameFramePacketsKeepNewestAndResetClearsHistory()
        {
            Call("Reset", 0d, Vector3.zero);
            Call("Add", .1d, Vector3.right);
            Call("Add", .1d, Vector3.right * 2);
            Assert.AreEqual(1f, At(.05d).x, .0001f);
            Call("Reset", 2d, Vector3.right * 10);
            Assert.AreEqual(10f, At(2d).x);
        }
        [Test] public void BufferRemainsBoundedDuringLargeBacklogs()
        {
            Call("Reset", 0d, Vector3.zero);
            for (int i = 1; i <= 100; i++) Call("Add", (double)i, Vector3.right * i);
            int count = (int)type.GetField("count", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(buffer);
            Assert.LessOrEqual(count, 32);
            Assert.AreEqual(99.5f, At(99.5d).x, .0001f);
        }
    }
}
