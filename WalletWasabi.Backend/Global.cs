﻿using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WalletWasabi.Crypto;
using System.Text;
using WalletWasabi.ChaumianCoinJoin;

namespace WalletWasabi.Backend
{
	public static class Global
	{
		private static string _dataDir = null;
		public static string DataDir
		{
			get
			{
				if (!string.IsNullOrWhiteSpace(_dataDir)) return _dataDir;

				_dataDir = EnvironmentHelpers.GetDataDir(Path.Combine("WalletWasabi","Backend"));

				return _dataDir;
			}
		}

		public static RPCClient RpcClient { get; private set; }

		public static BlindingRsaKey RsaKey { get; private set; }

		public static IndexBuilderService IndexBuilderService { get; private set; }

		public static CcjCoordinator Coordinator { get; private set; }

		public static ConfigWatcher RoundConfigWatcher { get; private set; }

		public static Config Config { get; private set; }

		public static CcjRoundConfig RoundConfig { get; private set; }

		public async static Task InitializeAsync(Config config, CcjRoundConfig roundConfig,  RPCClient rpc)
		{
			Config = Guard.NotNull(nameof(config), config);
			RoundConfig = Guard.NotNull(nameof(roundConfig), roundConfig);
			RpcClient = Guard.NotNull(nameof(rpc), rpc);

			// Initialize RsaKey
			string rsaKeyPath = Path.Combine(DataDir, "RsaKey.json");
			if (File.Exists(rsaKeyPath))
			{
				string rsaKeyJson = await File.ReadAllTextAsync(rsaKeyPath, encoding: Encoding.UTF8);
				RsaKey = BlindingRsaKey.CreateFromJson(rsaKeyJson);
			}
			else
			{
				RsaKey = new BlindingRsaKey();
				await File.WriteAllTextAsync(rsaKeyPath, RsaKey.ToJson(), encoding: Encoding.UTF8);
				Logger.LogInfo($"Created RSA key at: {rsaKeyPath}", nameof(Global));
			}

			await AssertRpcNodeFullyInitializedAsync();

			// Initialize index building
			var indexBuilderServiceDir = Path.Combine(DataDir, nameof(IndexBuilderService));
			var indexFilePath = Path.Combine(indexBuilderServiceDir, $"Index{RpcClient.Network}.dat");
			var utxoSetFilePath = Path.Combine(indexBuilderServiceDir, $"UtxoSet{RpcClient.Network}.dat");
			IndexBuilderService = new IndexBuilderService(RpcClient, indexFilePath, utxoSetFilePath);
			IndexBuilderService.Synchronize();

			Coordinator = new CcjCoordinator(RpcClient.Network, Path.Combine(DataDir,nameof(CcjCoordinator)), RpcClient, roundConfig);
			await Coordinator.MakeSureTwoRunningRoundsAsync();

			if (roundConfig.FilePath != null)
			{
				RoundConfigWatcher = new ConfigWatcher(RoundConfig);
				RoundConfigWatcher.Start(TimeSpan.FromSeconds(10), () => {
					try
					{
						Coordinator.UpdateRoundConfig(RoundConfig);

						Coordinator.FailAllRoundsInInputRegistration();
					}
					catch (Exception ex)
					{
						Logger.LogDebug<ConfigWatcher>(ex);
					}

					return Task.CompletedTask;
				}); // Every 10 seconds check the config
			}
		}

		private static async Task AssertRpcNodeFullyInitializedAsync()
		{
			try
			{
				var blockchainInfoRequest = new RPCRequest(RPCOperations.getblockchaininfo, parameters: null);
				var blockchainInfo = await RpcClient.GetBlockchainInfoAsync();
				
				var blocks = blockchainInfo.Blocks;
				if (blocks == 0 && Config.Network != Network.RegTest)
				{
					throw new NotSupportedException("blocks == 0");
				}

				var headers = blockchainInfo.Headers;
				if (headers == 0 && Config.Network != Network.RegTest)
				{
					throw new NotSupportedException("headers == 0");
				}

				if (blocks != headers)
				{
					throw new NotSupportedException("Bitcoin Core is not fully synchronized.");
				}

				Logger.LogInfo<RPCClient>("Bitcoin Core is fully synchronized.");

				var estimateSmartFeeResponse = await RpcClient.TryEstimateSmartFeeAsync(2, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true);
				if (estimateSmartFeeResponse == null) throw new NotSupportedException($"Bitcoin Core cannot estimate network fees yet.");
				Logger.LogInfo<RPCClient>("Bitcoin Core fee estimation is working.");

				if (Config.Network == Network.RegTest) // Make sure there's at least 101 block, if not generate it
				{
					if (blocks < 101)
					{
						var generateBlocksResponse = await RpcClient.GenerateAsync(101);
						if (generateBlocksResponse == null) throw new NotSupportedException($"Bitcoin Core cannot cannot generate blocks on the RegTest.");

						blockchainInfoRequest = new RPCRequest(RPCOperations.getblockchaininfo, parameters: null);
						blockchainInfo = await RpcClient.GetBlockchainInfoAsync();
						blocks = blockchainInfo.Blocks;
						if (blocks == 0)
						{
							throw new NotSupportedException("blocks == 0");
						}
						Logger.LogInfo<RPCClient>($"Generated 101 block on RegTest. Number of blocks {blocks}.");
					}
				}
			}
			catch(WebException)
			{
				Logger.LogInfo($"Bitcoin Core is not running, or incorrect RPC credentials or network is given in the config file: `{Config.FilePath}`.");
				throw;
			}
		}
	}
}
