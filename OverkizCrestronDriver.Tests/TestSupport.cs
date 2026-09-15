// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

using NUnit.Framework;

using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.SDK;

using OverKiz.CrestronDriver;

[assembly: LevelOfParallelism (1)]
[assembly: FixtureLifeCycle (LifeCycle.InstancePerTestCase)]

namespace OverkizCrestronDriver.Tests;

internal static class TestSupport
	{
	internal static T Call<T> (Type type, string name, params object[] args)
		{
		MethodInfo method = type.GetMethods (BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Single (m => m.Name == name && m.GetParameters ().Length == args.Length);
		try
			{
			return (T)method.Invoke (null, args);
			}
		catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture (e.InnerException).Throw (); throw; }
		}
	internal static async Task Complete (Task task)
		{
		Assert.That (await Task.WhenAny (task, Task.Delay (5000)), Is.SameAs (task), "Operation did not complete within five seconds.");
		await task;
		}
	internal static string DataDirectory
		{
		get
			{
			string root = TestContext.Parameters.Get ("TestDataDirectory", TestContext.CurrentContext.TestDirectory);
			string path = Path.Combine (root, "DriverTestData");
			if (!Directory.Exists (path)) path = Path.Combine (TestContext.CurrentContext.TestDirectory, "DriverTestData");
			if (!Directory.Exists (path)) path = Path.Combine (TestContext.CurrentContext.TestDirectory, "IncludeInPkg", "DriverTestData");
			Assert.That (Directory.Exists (path), Is.True, "Original driver UI test data is required.");
			return path;
			}
		}
	internal static DriverImplementationResources Resources (DriverLogger logger) => new ()
		{
		Logger = logger,
		InitLogger = logger.GetComponentLogger ("test", "driver"),
		DriverDefinition = Serialization.DefinitionFromJsonString (File.ReadAllText (Path.Combine (DataDirectory, "DriverDefinition.json"))),
		Conditions = new Dictionary<string, ICondition> (),
		Transformations = new Dictionary<string, ITransformation> (),
		TransportConfigItems = new Dictionary<string, IList<ConfigurationItemDefinition>> ()
		};
	}
