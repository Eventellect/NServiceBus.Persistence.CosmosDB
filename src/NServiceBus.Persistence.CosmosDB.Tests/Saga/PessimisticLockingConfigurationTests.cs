﻿namespace NServiceBus.Persistence.CosmosDB.Tests.Saga
{
    using System;
    using NUnit.Framework;

    [TestFixture]
    public class PessimisticLockingConfigurationTests
    {
        [Test]
        public void Should_have_default_values()
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.That(configuration.LeaseLockTime, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(configuration.LeaseLockAcquisitionTimeout, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(configuration.LeaseLockAcquisitionMinimumRefreshDelay, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
            Assert.That(configuration.LeaseLockAcquisitionMaximumRefreshDelay, Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
            Assert.That(configuration.PessimisticLockingEnabled, Is.False);
        }

        [Test]
        public void Should_throw_on_zero_value_for_lease_lock_time()
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockTime(TimeSpan.Zero));
        }

        [Test]
        public void Should_set_lease_lock_time([Random(1, 10, 5, Distinct = true)] double minutes)
        {
            var configuration = new PessimisticLockingConfiguration();

            var fromMinutes = TimeSpan.FromMinutes(minutes);

            configuration.SetLeaseLockTime(fromMinutes);

            Assert.AreEqual(fromMinutes, configuration.LeaseLockTime);
        }

        [Test]
        public void Should_throw_on_zero_value_for_lease_lock_acquisition_timeout()
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockAcquisitionTimeout(TimeSpan.Zero));
        }

        [Test]
        public void Should_set_lease_lock_acquisition_timeout([Random(1, 10, 5, Distinct = true)] double minutes)
        {
            var configuration = new PessimisticLockingConfiguration();

            var fromMinutes = TimeSpan.FromMilliseconds(minutes);

            configuration.SetLeaseLockTime(fromMinutes);

            Assert.AreEqual(fromMinutes, configuration.LeaseLockTime);
        }

        [Test]
        public void Should_throw_on_zero_value_for_minimum_refresh_delay()
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockAcquisitionMinimumRefreshDelay(TimeSpan.Zero));
        }

        [Test]
        public void Should_throw_bigger_value_than_maximum_refresh_delay_for_minimum_refresh_delay([Random(1001, 1100, 5, Distinct = true)] double milliseconds)
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockAcquisitionMinimumRefreshDelay(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Test]
        public void Should_set_minimum_refresh_delay([Random(1, 1000, 5, Distinct = true)] double milliseconds)
        {
            var configuration = new PessimisticLockingConfiguration();

            var fromMilliseconds = TimeSpan.FromMilliseconds(milliseconds);

            configuration.SetLeaseLockAcquisitionMinimumRefreshDelay(fromMilliseconds);

            Assert.AreEqual(fromMilliseconds, configuration.LeaseLockAcquisitionMinimumRefreshDelay);
        }

        [Test]
        public void Should_throw_on_zero_value_for_maximum_refresh_delay()
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockAcquisitionMaximumRefreshDelay(TimeSpan.Zero));
        }

        [Test]
        public void Should_throw_smaller_value_than_minimum_refresh_delay_for_maximum_refresh_delay([Random(1, 499, 5, Distinct = true)] double milliseconds)
        {
            var configuration = new PessimisticLockingConfiguration();

            Assert.Throws<ArgumentOutOfRangeException>(() => configuration.SetLeaseLockAcquisitionMaximumRefreshDelay(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Test]
        public void Should_set_maximum_refresh_delay([Random(500, 1500, 5, Distinct = true)] double milliseconds)
        {
            var configuration = new PessimisticLockingConfiguration();

            var fromMilliseconds = TimeSpan.FromMilliseconds(milliseconds);

            configuration.SetLeaseLockAcquisitionMaximumRefreshDelay(fromMilliseconds);

            Assert.AreEqual(fromMilliseconds, configuration.LeaseLockAcquisitionMaximumRefreshDelay);
        }
    }
}