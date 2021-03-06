﻿// -----------------------------------------------------------------------
//  <copyright file="AsyncDocumentSubscriptions.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions.Subscriptions;
using Raven.Client.Connection.Async;
using Raven.Client.Util;
using Raven.Database.Util;
using Raven.Json.Linq;

namespace Raven.Client.Document
{
	public class AsyncDocumentSubscriptions : IAsyncReliableSubscriptions
	{
		private readonly IDocumentStore documentStore;
		private readonly ConcurrentSet<IDisposableAsync> subscriptions = new ConcurrentSet<IDisposableAsync>();

		public AsyncDocumentSubscriptions(IDocumentStore documentStore)
		{
			this.documentStore = documentStore;
		}

		public Task<long> CreateAsync<T>(SubscriptionCriteria<T> criteria, string database = null)
		{
			if (criteria == null)
				throw new InvalidOperationException("Cannot create a subscription if criteria is null");

			var nonGenericCriteria = new SubscriptionCriteria();

			nonGenericCriteria.BelongsToAnyCollection = new []{ documentStore.Conventions.GetTypeTagName(typeof (T)) };
			nonGenericCriteria.KeyStartsWith = criteria.KeyStartsWith;
			nonGenericCriteria.PropertiesMatch = criteria.GetPropertiesMatchStrings();
			nonGenericCriteria.PropertiesNotMatch = criteria.GetPropertiesNotMatchStrings();

			return CreateAsync(nonGenericCriteria, database);
		}

		public async Task<long> CreateAsync(SubscriptionCriteria criteria, string database = null)
		{
			if (criteria == null)
				throw new InvalidOperationException("Cannot create a subscription if criteria is null");

			var commands = database == null
				? documentStore.AsyncDatabaseCommands
				: documentStore.AsyncDatabaseCommands.ForDatabase(database);

			using (var request = commands.CreateRequest("/subscriptions/create", "POST"))
			{
				await request.WriteAsync(RavenJObject.FromObject(criteria)).ConfigureAwait(false);

				return request.ReadResponseJson().Value<long>("Id");
			}
		}

		public Task<Subscription<RavenJObject>> OpenAsync(long id, SubscriptionConnectionOptions options, string database = null)
		{
			return OpenAsync<RavenJObject>(id, options, database);
		}

		public async Task<Subscription<T>> OpenAsync<T>(long id, SubscriptionConnectionOptions options, string database = null) where T : class
		{
			if(options == null)
				throw new InvalidOperationException("Cannot open a subscription if options are null");

			if(options.BatchOptions == null)
				throw new InvalidOperationException("Cannot open a subscription if batch options are null");

			if(options.BatchOptions.MaxSize.HasValue && options.BatchOptions.MaxSize.Value < 16 * 1024)
				throw new InvalidOperationException("Max size value of batch options cannot be lower than that 16 KB");

			var commands = database == null
				? documentStore.AsyncDatabaseCommands
				: documentStore.AsyncDatabaseCommands.ForDatabase(database);

			await SendOpenSubscriptionRequest(commands, id, options).ConfigureAwait(false);

			var subscription = new Subscription<T>(id, options, commands, documentStore.Changes(database), documentStore.Conventions, () => 
				SendOpenSubscriptionRequest(commands, id, options)); // to ensure that subscription is open try to call it with the same connection id

			subscriptions.Add(subscription);

			return subscription;
		}

		private static async Task SendOpenSubscriptionRequest(IAsyncDatabaseCommands commands, long id, SubscriptionConnectionOptions options)
		{
			using (var request = commands.CreateRequest(string.Format("/subscriptions/open?id={0}&connection={1}", id, options.ConnectionId), "POST"))
			{
				try
				{
					await request.WriteAsync(RavenJObject.FromObject(options)).ConfigureAwait(false);
					await request.ExecuteRequestAsync().ConfigureAwait(false);
				}
				catch (ErrorResponseException e)
				{
					SubscriptionException subscriptionException;
					if (TryGetSubscriptionException(e, out subscriptionException))
						throw subscriptionException;

					throw;
				}
			}
		}

		public async Task<List<SubscriptionConfig>> GetSubscriptionsAsync(int start, int take, string database = null)
		{
			var commands = database == null
				? documentStore.AsyncDatabaseCommands
				: documentStore.AsyncDatabaseCommands.ForDatabase(database);

			List<SubscriptionConfig> configs;

			using (var request = commands.CreateRequest("/subscriptions", "GET"))
			{
				var response = await request.ReadResponseJsonAsync().ConfigureAwait(false);

				configs = documentStore.Conventions.CreateSerializer().Deserialize<SubscriptionConfig[]>(new RavenJTokenReader(response)).ToList();
			}

			return configs;
		}

		public Task DeleteAsync(long id, string database = null)
		{
			var commands = database == null
				? documentStore.AsyncDatabaseCommands
				: documentStore.AsyncDatabaseCommands.ForDatabase(database);

			using (var request = commands.CreateRequest("/subscriptions?id=" + id, "DELETE"))
			{
				return request.ExecuteRequestAsync();
			}
		}

		public Task ReleaseAsync(long id, string database = null)
		{
			var commands = database == null
				? documentStore.AsyncDatabaseCommands
				: documentStore.AsyncDatabaseCommands.ForDatabase(database);

			using (var request = commands.CreateRequest(string.Format("/subscriptions/close?id={0}&connection=&force=true", id), "POST"))
			{
				return request.ExecuteRequestAsync();
			}
		}

		public static bool TryGetSubscriptionException(ErrorResponseException ere, out SubscriptionException subscriptionException)
		{
			if (ere.StatusCode == SubscriptionDoesNotExistExeption.RelevantHttpStatusCode)
			{
				subscriptionException = new SubscriptionDoesNotExistExeption(ere.ResponseString);
				return true;
			}

			if (ere.StatusCode == SubscriptionInUseException.RelavantHttpStatusCode)
			{
				subscriptionException = new SubscriptionInUseException(ere.Message);
				return true;
			}

			if (ere.StatusCode == SubscriptionClosedException.RelevantHttpStatusCode)
			{
				subscriptionException = new SubscriptionClosedException(ere.Message);
				return true;
			}

			subscriptionException = null;
			return false;
		}

		public void Dispose()
		{
			var tasks = new List<Task>();

			foreach (var subscription in subscriptions)
			{
				tasks.Add(subscription.DisposeAsync());
			}

			Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
		}
	}
}