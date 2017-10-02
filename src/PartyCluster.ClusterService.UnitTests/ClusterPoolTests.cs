﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace PartyCluster.ClusterService.UnitTests
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Domain;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Mocks;
    using Pool;

    [TestClass]
    public class ClusterPoolTests
    {
        private Random random = new Random(7);
        private object locker = new object();

        /// <summary>
        /// First time around there are no clusters. This tests that the minimum number of clusters is created initially.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClusters()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager, 
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            await clusterPool.BalanceClustersAsync(config.MinimumClusterCount, CancellationToken.None);

            ConditionalValue<IReliableDictionary<int, Cluster>> result =
                await stateManager.TryGetAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(config.MinimumClusterCount, await result.Value.GetCountAsync(null));
            Assert.IsTrue((
                await result.Value.CreateEnumerableAsync(null))
                .ToEnumerable()
                .All(x => x.Value.Status == ClusterStatus.New));
        }

        /// <summary>
        /// The total amount of active clusters should never go below the min threshold.
        /// When the current active cluster count is below min, and the new target is greater than current but still below min, bump the amount up to min.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersIncreaseBelowMin()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int readyCount = (int)Math.Floor(config.MinimumClusterCount / 5D);
            int newCount = readyCount;
            int creatingCount = readyCount;

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyCount, ClusterStatus.Ready);
                await this.AddClusters(tx, dictionary, newCount, ClusterStatus.New);
                await this.AddClusters(tx, dictionary, creatingCount, ClusterStatus.Creating);
                await this.AddClusters(tx, dictionary, config.MinimumClusterCount, ClusterStatus.Deleting);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(readyCount * 4, CancellationToken.None);
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual<int>(
                    config.MinimumClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(
                        x =>
                            x.Value.Status == ClusterStatus.Ready ||
                            x.Value.Status == ClusterStatus.New ||
                            x.Value.Status == ClusterStatus.Creating));
            }
        }

        /// <summary>
        /// The total amount of active clusters should never go below the min threshold.
        /// When the request amount is below the minimum threshold, and the new target is less than current, bump the amount up to min.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersDecreaseBelowMin()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int readyCount = config.MinimumClusterCount - 1;
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyCount, ClusterStatus.Ready);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(config.MinimumClusterCount - 2, CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual<int>(
                    readyCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Ready));

                Assert.AreEqual(
                    config.MinimumClusterCount - readyCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.New));
            }
        }

        /// <summary>
        /// The total amount of active clusters should never go below the min threshold.
        /// When the request amount is above the minimum threshold, and the new target is less than min, only remove down to min.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersMinThreshold()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, config.MinimumClusterCount, ClusterStatus.Ready);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(config.MinimumClusterCount - 1, CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(config.MinimumClusterCount, await dictionary.GetCountAsync(tx));
                Assert.IsTrue((await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().All(x => x.Value.Status == ClusterStatus.Ready));
            }
        }

        /// <summary>.
        /// The total amount of active clusters should never go above the max threshold.
        /// Only add clusters up to the limit considering only active clusters.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersMaxThreshold()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            int readyClusters = 10;
            int deletingClusterCount = 20;
            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyClusters, ClusterStatus.Ready);
                await this.AddClusters(tx, dictionary, deletingClusterCount, ClusterStatus.Deleting);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(config.MaximumClusterCount + 1, CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(config.MaximumClusterCount + deletingClusterCount, await dictionary.GetCountAsync(tx));

                Assert.AreEqual(
                    config.MaximumClusterCount - readyClusters,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.New));

                Assert.AreEqual(
                    readyClusters,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Ready));
            }
        }

        /// <summary>.
        /// The total amount of active clusters should never go above the max threshold.
        /// When the total active clusters is above max, and the new total is greater than current, remove clusters down to the minimum.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersIncreaseAboveMax()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            int aboveMax = 10;
            int readyClusters = config.MaximumClusterCount + aboveMax;
            int deletingClusterCount = 20;
            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyClusters, ClusterStatus.Ready);
                await this.AddClusters(tx, dictionary, deletingClusterCount, ClusterStatus.Deleting);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(readyClusters + 10, CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(
                    config.MaximumClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Ready));

                Assert.AreEqual(
                    aboveMax,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Remove));

                Assert.AreEqual(
                    deletingClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Deleting));
            }
        }

        /// <summary>.
        /// The total amount of active clusters should never go above the max threshold.
        /// When the total active clusters is above max, and the new total is greater than current, remove clusters down to the minimum.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersDecreaseAboveMax()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            int aboveMax = 10;
            int readyClusters = config.MaximumClusterCount + aboveMax;
            int deletingClusterCount = 20;
            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyClusters, ClusterStatus.Ready);
                await this.AddClusters(tx, dictionary, deletingClusterCount, ClusterStatus.Deleting);
                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(readyClusters - (aboveMax / 2), CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(
                    config.MaximumClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Ready));
                Assert.AreEqual(
                    aboveMax,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Remove));
                Assert.AreEqual(
                    deletingClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Deleting));
            }
        }

        /// <summary>
        /// Tests that only active clusters are considered for removal without going below the minimum threshold.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClusterDecreaseAlreadyDeleting()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int readyCount = 5 + config.MinimumClusterCount;
            int deletingCount = 10;
            int targetCount = config.MinimumClusterCount / 2;

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(tx, dictionary, readyCount, ClusterStatus.Ready);
                await this.AddClusters(tx, dictionary, deletingCount, ClusterStatus.Deleting);

                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(targetCount, CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(
                    readyCount - config.MinimumClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Remove));

                Assert.AreEqual(
                    config.MinimumClusterCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Ready));

                Assert.AreEqual(
                    deletingCount,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Count(x => x.Value.Status == ClusterStatus.Deleting));
            }
        }

        /// <summary>
        /// BalanceClustersAsync should not flag to remove clusters that still have users in them 
        /// when given a target count below the current count.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task BalanceClustersDecreaseNonEmpty()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int withUsers = config.MinimumClusterCount + 5;
            int withoutUsers = 10;
            int targetCount = (withUsers + withoutUsers) - 11;

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(
                    tx,
                    dictionary,
                    withUsers,
                    () => this.CreateCluster(
                        ClusterStatus.Ready,
                        new List<ClusterUser>() { new ClusterUser() }));

                await this.AddClusters(tx, dictionary, withoutUsers, ClusterStatus.Ready);

                await tx.CommitAsync();
            }

            await clusterPool.BalanceClustersAsync(targetCount, CancellationToken.None);
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Assert.AreEqual(
                    withUsers,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Select(x => x.Value).Count(x => x.Status == ClusterStatus.Ready));

                Assert.AreEqual(
                    withoutUsers,
                    (await dictionary.CreateEnumerableAsync(tx)).ToEnumerable().Select(x => x.Value).Count(x => x.Status == ClusterStatus.Remove));
            }
        }

        /// <summary>
        /// When the number of users in all the clusters reaches the UserCapacityHighPercentThreshold value, 
        /// increase the number of clusters by (100 - UserCapacityHighPercentThreshold)%
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TargetClusterCapacityIncrease()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int clusterCount = config.MinimumClusterCount;
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(
                    tx,
                    dictionary,
                    clusterCount,
                    () => this.CreateCluster(
                        ClusterStatus.Ready,
                        new List<ClusterUser>(
                            Enumerable.Repeat(
                                new ClusterUser(),
                                (int)Math.Ceiling((double)config.MaximumUsersPerCluster * config.UserCapacityHighPercentThreshold)))));

                await this.AddClusters(
                    tx,
                    dictionary,
                    5,
                    () => this.CreateCluster(
                        ClusterStatus.Remove,
                        new List<ClusterUser>(Enumerable.Repeat(new ClusterUser(), config.MaximumUsersPerCluster))));

                await this.AddClusters(tx, dictionary, 5, ClusterStatus.Deleting);

                await tx.CommitAsync();
            }

            int expected = clusterCount + (int)Math.Ceiling(clusterCount * (1 - config.UserCapacityHighPercentThreshold));
            int actual = await clusterPool.GetTargetClusterCapacityAsync(CancellationToken.None);

            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// When the number of users in all the clusters reaches the UserCapacityHighPercentThreshold value, 
        /// increase the number of clusters without going over MaximumClusterCount. 
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TargetClusterCapacityIncreaseAtMaxCount()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int clusterCount = config.MaximumClusterCount - 1;
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(
                    tx,
                    dictionary,
                    clusterCount,
                    () => this.CreateCluster(
                        ClusterStatus.Ready,
                        new List<ClusterUser>(
                            Enumerable.Repeat(
                                new ClusterUser(),
                                (int)Math.Ceiling((double)config.MaximumUsersPerCluster * config.UserCapacityHighPercentThreshold)))));

                await this.AddClusters(
                    tx,
                    dictionary,
                    5,
                    () => this.CreateCluster(
                        ClusterStatus.Remove,
                        new List<ClusterUser>(Enumerable.Repeat(new ClusterUser(), config.MaximumUsersPerCluster))));

                await tx.CommitAsync();
            }

            int actual = await clusterPool.GetTargetClusterCapacityAsync(CancellationToken.None);

            Assert.AreEqual(config.MaximumClusterCount, actual);
        }

        /// <summary>
        /// When the number of users in all the clusters reaches the UserCapacityLowPercentThreshold value, 
        /// decrease the number of clusters by high-low% capacity
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TargetClusterCapacityDecrease()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int clusterCount = config.MaximumClusterCount;
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(
                    tx,
                    dictionary,
                    clusterCount,
                    () => this.CreateCluster(
                        ClusterStatus.Ready,
                        new List<ClusterUser>(
                            Enumerable.Repeat(
                                new ClusterUser(),
                                (int)Math.Floor((double)config.MaximumUsersPerCluster * config.UserCapacityLowPercentThreshold)))));

                await this.AddClusters(
                    tx,
                    dictionary,
                    5,
                    () => this.CreateCluster(
                        ClusterStatus.Remove,
                        new List<ClusterUser>(Enumerable.Repeat(new ClusterUser(), config.MaximumUsersPerCluster))));

                await tx.CommitAsync();
            }

            int expected = clusterCount - (int)Math.Floor(clusterCount * (config.UserCapacityHighPercentThreshold - config.UserCapacityLowPercentThreshold));
            int actual = await clusterPool.GetTargetClusterCapacityAsync(CancellationToken.None);

            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// When the number of users in all the clusters reaches the UserCapacityLowPercentThreshold value, 
        /// decrease the number of clusters without going below the min threshold.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task TargetClusterCapacityDecreaseAtMinCount()
        {
            ClusterConfig config = new ClusterConfig();
            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            int clusterCount = config.MinimumClusterCount + 1;
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await this.AddClusters(
                    tx,
                    dictionary,
                    clusterCount,
                    () => this.CreateCluster(
                        ClusterStatus.Ready,
                        new List<ClusterUser>(
                            Enumerable.Repeat(
                                new ClusterUser(),
                                (int)Math.Floor((double)config.MaximumUsersPerCluster * config.UserCapacityLowPercentThreshold)))));

                await tx.CommitAsync();
            }

            int expected = config.MinimumClusterCount;
            int actual = await clusterPool.GetTargetClusterCapacityAsync(CancellationToken.None);

            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// Make sure ProcessClustersAsync is saving the updated cluster object.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessClustersAsyncSaveChanges()
        {
            int key = 1;
            string nameTemplate = "Test:{0}";
            MockReliableStateManager stateManager = new MockReliableStateManager();
            ClusterConfig config = new ClusterConfig();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                CreateClusterAsyncFunc = (name, ports) => { return Task.FromResult(String.Format(nameTemplate, name)); }
            };

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            Cluster original = this.CreateCluster(ClusterStatus.New);
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await dictionary.SetAsync(tx, key, original);
            }

            await clusterPool.ProcessClustersAsync(CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                Cluster actual = (await dictionary.TryGetValueAsync(tx, key)).Value;

                Assert.AreNotEqual(original, actual);
            }
        }

        /// <summary>
        /// Make sure ProcessClustersAsync is saving the updated cluster object.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessClustersAsyncDelete()
        {
            int key = 1;
            MockReliableStateManager stateManager = new MockReliableStateManager();
            ClusterConfig config = new ClusterConfig();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                GetClusterStatusAsyncFunc = (name) => Task.FromResult(ClusterOperationStatus.ClusterNotFound)
            };

            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            IReliableDictionary<int, Cluster> dictionary =
                await stateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(clusterPool.DictionaryName);

            Cluster original = this.CreateCluster(ClusterStatus.Deleting);
            using (ITransaction tx = stateManager.CreateTransaction())
            {
                await dictionary.SetAsync(tx, key, original);
            }

            await clusterPool.ProcessClustersAsync(CancellationToken.None);

            using (ITransaction tx = stateManager.CreateTransaction())
            {
                ConditionalValue<Cluster> actual = await dictionary.TryGetValueAsync(tx, key);

                Assert.IsFalse(actual.HasValue);
            }
        }

        /// <summary>
        /// A new cluster should initiate a create cluster operation and switch its status to "creating" if successful.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessNewCluster()
        {
            bool calledActual = false;
            string nameTemplate = "Test:{0}";
            string nameActual = null;
            IEnumerable<int> portsExpected = Enumerable.Empty<int>();

            MockReliableStateManager stateManager = new MockReliableStateManager();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                CreateClusterAsyncFunc = (name, ports) =>
                {
                    nameActual = name;
                    portsExpected = ports;
                    calledActual = true;
                    return Task.FromResult(String.Format(nameTemplate, name));
                }
            };

            ClusterConfig config = new ClusterConfig();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            Cluster cluster = this.CreateCluster(ClusterStatus.New);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.IsTrue(calledActual);
            Assert.AreEqual(ClusterStatus.Creating, actual.Status);
            Assert.AreEqual(String.Format(nameTemplate, nameActual), actual.Address);
            Assert.AreEqual(5, actual.Ports.Count());
            Enumerable.SequenceEqual(portsExpected, actual.Ports);
        }

        /// <summary>
        /// A creating cluster should set its status to ready and populate fields when the cluster creation has completed.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessCreatingClusterSuccess()
        {
            MockReliableStateManager stateManager = new MockReliableStateManager();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                GetClusterStatusAsyncFunc = name => Task.FromResult(ClusterOperationStatus.Ready)
            };

            ClusterConfig config = new ClusterConfig();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            Cluster cluster = this.CreateCluster(ClusterStatus.Creating);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.AreEqual(ClusterStatus.Ready, actual.Status);
            Assert.IsTrue(actual.CreatedOn.ToUniversalTime() <= DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// A creating cluster should set the status to "remove" if creation failed so that the failed deployment can be deleted.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessCreatingClusterFailed()
        {
            MockReliableStateManager stateManager = new MockReliableStateManager();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                GetClusterStatusAsyncFunc = name => Task.FromResult(ClusterOperationStatus.CreateFailed)
            };

            ClusterConfig config = new ClusterConfig();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);
            Cluster cluster = this.CreateCluster(ClusterStatus.Creating);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.AreEqual(ClusterStatus.Remove, actual.Status);
            Assert.AreEqual(0, actual.Ports.Count());
            Assert.AreEqual(0, actual.Users.Count());
        }

        /// <summary>
        /// A cluster marked for removal should initiate a delete cluster operation and switch its status to "deleting" if successful.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessRemove()
        {
            bool calledActual = false;
            string nameActual = null;

            MockReliableStateManager stateManager = new MockReliableStateManager();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                DeleteClusterAsyncFunc = name =>
                {
                    nameActual = name;
                    calledActual = true;
                    return Task.FromResult(true);
                }
            };

            ClusterConfig config = new ClusterConfig();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            Cluster cluster = this.CreateCluster(ClusterStatus.Remove);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.IsTrue(calledActual);
            Assert.AreEqual(ClusterStatus.Deleting, actual.Status);
        }

        /// <summary>
        /// When deleting is complete, set the status to deleted.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessDeletingSuccessful()
        {
            MockReliableStateManager stateManager = new MockReliableStateManager();
            MockClusterOperator clusterOperator = new MockClusterOperator()
            {
                GetClusterStatusAsyncFunc = domain => Task.FromResult(ClusterOperationStatus.ClusterNotFound)
            };

            ClusterConfig config = new ClusterConfig();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", clusterOperator, config);

            Cluster cluster = this.CreateCluster(ClusterStatus.Deleting);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.AreEqual(ClusterStatus.Deleted, actual.Status);
        }

        /// <summary>
        /// A cluster should be marked for removal when its time limit has elapsed.
        /// </summary>
        /// <returns></returns>
        [TestMethod]
        public async Task ProcessRemoveTimeLimit()
        {
            ClusterConfig config = new ClusterConfig()
            {
                MaximumClusterUptime = TimeSpan.FromHours(2)
            };

            MockReliableStateManager stateManager = new MockReliableStateManager();
            var clusterPool = new ClusterPool("UnitTestClusterPool", stateManager,
                "UnitTestPoolDictionary", new FakeClusterOperator(stateManager), config);

            Cluster cluster = new Cluster(
                "test",
                ClusterStatus.Ready,
                0,
                0,
                String.Empty,
                new int[0],
                new ClusterUser[0],
                DateTimeOffset.MinValue,
                DateTimeOffset.UtcNow - config.MaximumClusterUptime);

            Cluster actual = await clusterPool.ProcessClusterStatusAsync(cluster);

            Assert.AreEqual(ClusterStatus.Remove, actual.Status);
        }

        private async Task AddClusters(ITransaction tx, IReliableDictionary<int, Cluster> dictionary, int count, Func<Cluster> newCluster)
        {
            for (int i = 0; i < count; ++i)
            {
                await dictionary.AddAsync(tx, this.GetRandom(), newCluster());
            }
        }

        private Task AddClusters(ITransaction tx, IReliableDictionary<int, Cluster> dictionary, int count, ClusterStatus status)
        {
            return this.AddClusters(tx, dictionary, count, () => this.CreateCluster(status));
        }

        private Cluster CreateCluster(ClusterStatus status)
        {
            return new Cluster(
                status,
                new Cluster("test"));
        }

        private Cluster CreateCluster(ClusterStatus status, IEnumerable<ClusterUser> users)
        {
            return new Cluster(
                "test",
                status,
                0,
                0,
                String.Empty,
                new int[0],
                users,
                DateTimeOffset.MaxValue,
                DateTimeOffset.MaxValue);
        }

        private StatefulServiceContext CreateServiceContext()
        {
            return new StatefulServiceContext(
                new NodeContext(String.Empty, new NodeId(0, 0), 0, String.Empty, String.Empty),
                new MockCodePackageActivationContext(),
                String.Empty,
                new Uri("fabric:/Mock"),
                null,
                Guid.NewGuid(),
                0);
        }

        private int GetRandom()
        {
            lock (this.locker)
            {
                return this.random.Next();
            }
        }
    }
}