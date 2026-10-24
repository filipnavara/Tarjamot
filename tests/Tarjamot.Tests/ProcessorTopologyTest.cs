using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class ProcessorTopologyTest
{
	[Test]
	public void PhysicalCoresAreWithinLogicalProcessors()
	{
		int cores = ProcessorTopology.PhysicalCores;
		TestContext.Out.WriteLine($"{cores} physical cores, {Environment.ProcessorCount} logical processors");

		Assert.That(cores, Is.InRange(1, Environment.ProcessorCount));
		// Two-way SMT at most on current hardware.
		Assert.That(cores * 2, Is.GreaterThanOrEqualTo(Environment.ProcessorCount));
	}
}
