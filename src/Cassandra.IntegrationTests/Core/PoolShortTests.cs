using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.IntegrationTests.Policies.Util;
using Cassandra.IntegrationTests.TestBase;
using NUnit.Framework;
using Cassandra.IntegrationTests.TestClusterManagement;
using Cassandra.Tasks;
using Cassandra.Tests;

namespace Cassandra.IntegrationTests.Core
{
    [TestFixture, Category("short"), Category("debug")]
    public class PoolShortTests : TestGlobals
    {
        [TestFixtureSetUp]
        public void OnTestFixtureSetUp()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!Unhandled exception: {0}; Is terminating: {1}", e.ExceptionObject, e.IsTerminating);
        }

        [TearDown]
        public void OnTearDown()
        {
            TestClusterManager.TryRemove();
        }

        [Test, Timeout(1000 * 60 * 8)]
        public void StopForce_With_Inflight_Requests([Range(1, 30)]int r)
        {
            if (TestUtils.IsWin && CassandraVersion.Major < 3)
            {
                Assert.Ignore("On Windows this test should run against C* 3.0+");
            }
            var testCluster = TestClusterManager.CreateNew(2);
            var builder = Cluster.Builder()
                .AddContactPoint(testCluster.InitialContactPoint)
                .WithPoolingOptions(new PoolingOptions()
                    .SetCoreConnectionsPerHost(HostDistance.Local, 4)
                    .SetMaxConnectionsPerHost(HostDistance.Local, 4))
                .WithRetryPolicy(AlwaysIgnoreRetryPolicy.Instance)
                .WithSocketOptions(new SocketOptions().SetReadTimeoutMillis(0))
                .WithLoadBalancingPolicy(new RoundRobinPolicy());
            using (var cluster = builder.Build())
            {
                var session = (Session)cluster.Connect();
                session.Execute(string.Format(TestUtils.CreateKeyspaceSimpleFormat, "ks1", 2));
                session.Execute("CREATE TABLE ks1.table1 (id1 int, id2 int, PRIMARY KEY (id1, id2))");
                var ps = session.Prepare("INSERT INTO ks1.table1 (id1, id2) VALUES (?, ?)");
                Console.WriteLine("--Warmup");
                Task.Factory.StartNew(() => ExecuteMultiple(testCluster, session, ps, false, 1, 2).Wait()).Wait();
                Console.WriteLine("--Starting");
                Task.Factory.StartNew(() => ExecuteMultiple(testCluster, session, ps, true, 8000, 200000).Wait()).Wait();
            }
        }

        private Task<bool> ExecuteMultiple(ITestCluster testCluster, Session session, PreparedStatement ps, bool stopNode, int maxConcurrency, int repeatLength)
        {
            var tcs = new TaskCompletionSource<bool>();
            var receivedCounter = 0;
            var sendCounter = 0;
            var currentlySentCounter = 0;
            var halfway = repeatLength / 2;
            var timer = new Timer(_ =>
            {
                Console.WriteLine("Received {0}", Thread.VolatileRead(ref receivedCounter));
            }, null, 60000L, 60000L);
            Action sendNew = null;
            sendNew = () =>
            {
                var sent = Interlocked.Increment(ref sendCounter);
                if (sent > repeatLength)
                {
                    return;
                }
                Interlocked.Increment(ref currentlySentCounter);
                var statement = ps.Bind(sent % 100, sent);
                var executeTask = session.ExecuteAsync(statement);
                executeTask.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        tcs.TrySetException(t.Exception.InnerException);
                        return;
                    }
                    var received = Interlocked.Increment(ref receivedCounter);
                    if (stopNode && received == halfway)
                    {
                        Console.WriteLine("--Stopping forcefully node2");
                        testCluster.StopForce(2);
                    }
                    if (received == repeatLength)
                    {
                        // Mark this as finished
                        Console.WriteLine("--Marking as completed");
                        timer.Dispose();
                        tcs.TrySetResult(true);
                        return;
                    }
                    sendNew();
                }, TaskContinuationOptions.ExecuteSynchronously);
            };

            for (var i = 0; i < maxConcurrency; i++)
            {
                sendNew();
            }
            return tcs.Task;
        }
        
        /// <summary>
        /// Async semaphore implementation, as its not available in the .NET Framework 
        /// </summary>
        private class AsyncSemaphore
        {
            private readonly Queue<TaskCompletionSource<bool>> _waiters = new Queue<TaskCompletionSource<bool>>();
            private int _currentCount;

            public AsyncSemaphore(int initialCount)
            {
                if (initialCount < 0)
                {
                    throw new ArgumentOutOfRangeException("initialCount");
                }
                _currentCount = initialCount;
            }

            public Task WaitAsync()
            {
                lock (_waiters)
                {
                    if (_currentCount > 0)
                    {
                        --_currentCount;
                        return TaskHelper.Completed;
                    }
                    var w = new TaskCompletionSource<bool>();
                    _waiters.Enqueue(w);
                    return w.Task;
                }
            }

            public void Release()
            {
                TaskCompletionSource<bool> toRelease = null;
                lock (_waiters)
                {
                    if (_waiters.Count > 0)
                    {
                        toRelease = _waiters.Dequeue();
                    }
                    else
                    {
                        ++_currentCount;
                    }
                }
                if (toRelease != null)
                {
                    toRelease.SetResult(true);
                }
            }
        }
    }
}