﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ServiceStack.Logging;
using ServiceStack.Redis;

namespace HangFire.Server
{
    internal class JobManager
    {
        private readonly List<Worker> _workers;
        private readonly BlockingCollection<Worker> _freeWorkers;

        private readonly ServerContext _context;
        private readonly WorkerPool _pool;
        private readonly Thread _managerThread;
        private readonly JobFetcher _fetcher;

        private readonly IRedisClient _redis = RedisFactory.Create();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ILog _logger = LogManager.GetLogger(typeof (JobManager));

        private bool _stopSent;
        
        public JobManager(ServerContext context, WorkerPool pool)
        {
            _context = context;
            _pool = pool;

            _workers = new List<Worker>(pool.WorkersCount);
            _freeWorkers = new BlockingCollection<Worker>();

            _logger.Info(String.Format("Starting {0} workers...", pool.WorkersCount));

            for (var i = 0; i < pool.WorkersCount; i++)
            {
                _workers.Add(
                    new Worker(this, new WorkerContext(context, i)));
            }

            _logger.Info("Workers were started.");

            _fetcher = new JobFetcher(_redis, pool.Queue);

            _managerThread = new Thread(Work)
                {
                    Name = typeof(JobManager).Name,
                    IsBackground = true
                };
            _managerThread.Start();
        }

        public void SendStop()
        {
            _stopSent = true;

            _cts.Cancel();

            foreach (var worker in _workers)
            {
                worker.SendStop();
            }
        }

        public void Dispose()
        {
            if (!_stopSent)
            {
                SendStop();
            }

            _managerThread.Join();

            foreach (var worker in _workers)
            {
                worker.Dispose();
            }
            _logger.Info("Workers were stopped.");

            _freeWorkers.Dispose();
            
            _redis.Dispose();
            _cts.Dispose();
        }

        internal void NotifyReady(Worker worker)
        {
            _freeWorkers.Add(worker);
        }

        private void Work()
        {
            try
            {
                while (true)
                {
                    Worker worker;
                    do
                    {
                        worker = _freeWorkers.Take(_cts.Token);
                    }
                    while (worker.Crashed);

                    JobPayload jobId;
                    do
                    {
                        jobId = _fetcher.DequeueJob();
                        if (jobId == null)
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                        }
                    } while (jobId == null);

                    worker.Process(jobId);
                }
            }
            catch (OperationCanceledException)
            {
                // TODO: log it
            }
            catch (Exception ex)
            {
                _logger.Fatal(
                    String.Format(
                        "Unexpected exception caught. Jobs from the queue '{0}' will not be processed by this server.", 
                        _pool.Queue),
                    ex);
            }
        }
    }
}
