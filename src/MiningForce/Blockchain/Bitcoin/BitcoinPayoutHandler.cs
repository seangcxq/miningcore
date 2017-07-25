﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using CodeContracts;
using MiningForce.Blockchain.Daemon;
using MiningForce.Configuration;
using MiningForce.Extensions;
using MiningForce.Payments;
using MiningForce.Persistence;
using MiningForce.Persistence.Model;
using MiningForce.Persistence.Repositories;
using MiningForce.Util;
using BDC = MiningForce.Blockchain.Bitcoin.BitcoinDaemonCommands;

namespace MiningForce.Blockchain.Bitcoin
{
    public class BitcoinPayoutHandler : PayoutHandlerBase,
		IPayoutHandler
	{
		public BitcoinPayoutHandler(IConnectionFactory cf, IMapper mapper,
			DaemonClient daemon,
			IShareRepository shareRepo, 
			IBlockRepository blockRepo,
			IBalanceRepository balanceRepo,
			IPaymentRepository paymentRepo) :
			base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo)
		{
			Contract.RequiresNonNull(daemon, nameof(daemon));
			Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
			Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));

			this.daemon = daemon;

		}

		private PoolConfig poolConfig;
		private readonly DaemonClient daemon;

		protected override string LogCategory => "Bitcoin Payout Handler";

		#region IPayoutHandler

		public void Configure(PoolConfig poolConfig)
		{
			Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

			this.poolConfig = poolConfig;
			logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinPayoutHandler), poolConfig);

			daemon.Configure(poolConfig);
		}

		public string FormatRewardAmount(decimal amount)
		{
			// assumes amount in satoshis (as returned from GetTransaction)
			return $"{amount:0.#####} {poolConfig.Coin.Type}";
		}

		public async Task<Block[]> ClassifyBlocksAsync(Block[] blocks)
		{
			Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
			Contract.RequiresNonNull(blocks, nameof(blocks));

			var pageSize = 100;
			var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
			var result = new List<Block>();

			for (int i = 0; i < pageCount; i++)
			{
				// get a page full of blocks
				var page = blocks
					.Skip(i * pageSize)
					.Take(pageCount)
					.ToArray();

				// build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
				var batch = page.Select(block => new DaemonCmd(BDC.GetTransaction,
					new[] {block.TransactionConfirmationData})).ToArray();

				// execute batch
				var results = await daemon.ExecuteBatchAnyAsync(batch);

				for (int j = 0; j < results.Length; j++)
				{
					var cmdResult = results[j];

					var transactionInfo = cmdResult.Response?.ToObject<DaemonResults.GetTransactionResult>();
					var block = page[j];

					// check error
					if (cmdResult.Error != null)
					{
						// Code -5 interpreted as "orphaned"
						if (cmdResult.Error.Code == -5)
						{
							block.Status = BlockStatus.Orphaned;
							result.Add(block);
						}

						else
						{
							logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
							continue;
						}
					}

					// missing transaction details are interpreted as "orphaned"
					else if (transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
					{
						block.Status = BlockStatus.Orphaned;
						result.Add(block);
					}

					else
					{
						switch (transactionInfo.Details[0].Category)
						{
							case "immature":
								// coinbase transaction that is not spendable yet, do nothing and let it mature
								break;

							case "generate":
								// matured and spendable coinbase transaction
								block.Status = BlockStatus.Confirmed;
								block.Reward = transactionInfo.Details[0].Amount / BitcoinConstants.SatoshisPerBitcoin;
								result.Add(block);
								break;

							default:
								block.Status = BlockStatus.Orphaned;
								result.Add(block);
								break;
						}
					}
				}
			}

			return result.ToArray();
		}

		public async Task PayoutAsync(Balance[] balances)
		{
			Contract.RequiresNonNull(balances, nameof(balances));

			logger.Info(()=> $"[{LogCategory}] Paying out {FormatRewardAmount(balances.Sum(x=> x.Amount))} to {balances.Length} addresses");

			// build args
			var amounts = balances.ToDictionary(x => x.Address, x => x.Amount);
			var subtractFeesFrom = amounts.Keys.ToArray();

			var args = new object[]
			{
				string.Empty,
				amounts,
				1,						// only spend funds covered by this many confirmations
				"MiningForce Payout",
				subtractFeesFrom
			};

			// send it
			var result = await daemon.ExecuteCmdAnyAsync<string>(BDC.SendMany, args);

			if (result.Error == null)
			{
				var txId = result.Response;

				// check result
				if (string.IsNullOrEmpty(txId))
					logger.Error(() => $"[{LogCategory}] 'sendmany' did not return a transaction id!");
				else
					logger.Info(() => $"[{LogCategory}] Payout Transaction Id is {txId}");

				// record changes
				cf.RunTx((con, tx) =>
				{
					foreach (var balance in balances)
					{
						// record payment
						var payment = new Payment
						{
							PoolId = poolConfig.Id,
							Coin = poolConfig.Coin.Type,
							Address = balance.Address,
							Amount = balance.Amount,
							Created = DateTime.UtcNow,
							TransactionConfirmationData = txId,
						};

						paymentRepo.Insert(con, tx, payment);

						// subtract balance
						balanceRepo.AddAmount(con, tx, poolConfig.Id, poolConfig.Coin.Type, balance.Address, -balance.Amount);
					}
				});
			}

			else
				logger.Error(() => $"[{LogCategory}] SendMany returned error: {result.Error.Message} Code {result.Error.Code}");
		}

		#endregion // IPayoutHandler
	}
}
