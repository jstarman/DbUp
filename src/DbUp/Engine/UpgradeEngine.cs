using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbUp.Builder;

namespace DbUp.Engine
{
    /// <summary>
    /// This class orchestrates the database upgrade process.
    /// </summary>
    public class UpgradeEngine
    {
        private readonly UpgradeConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="UpgradeEngine"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        public UpgradeEngine(UpgradeConfiguration configuration)
        {
            this.configuration = configuration;
        }

        /// <summary>
        /// Determines whether the database is out of date and can be upgraded.
        /// </summary>
        public bool IsUpgradeRequired()
        {
            return GetScriptsToExecute().Count() != 0;
        }

        /// <summary>
        /// Tries to connect to the database.
        /// </summary>
        /// <param name="errorMessage">Any error message encountered.</param>
        /// <returns></returns>
        public bool TryConnect(out string errorMessage)
        {
            return configuration.ConnectionManager.TryConnect(configuration.Log, out errorMessage);
        }

        /// <summary>
        /// Performs the database upgrade.
        /// </summary>
        public DatabaseUpgradeResult PerformUpgrade()
        {
            var executed = new List<SqlScript>();

            string executedScriptName = null;
            try
            {
                using (configuration.ConnectionManager.OperationStarting(configuration.Log, executed))
                {

                    configuration.Log.WriteInformation("Beginning database upgrade");

                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    if (scriptsToExecute.Count == 0)
                    {
                        configuration.Log.WriteInformation("No new scripts need to be executed - completing.");
                        return new DatabaseUpgradeResult(executed, true, null);
                    }

                    configuration.ScriptExecutor.VerifySchema();

                    foreach (var script in scriptsToExecute)
                    {
                        executedScriptName = script.Name;

                        configuration.ScriptExecutor.Execute(script, configuration.Variables);

                        configuration.Journal.StoreExecutedScript(script);

                        executed.Add(script);
                    }

                    configuration.Log.WriteInformation("Upgrade successful");
                    return new DatabaseUpgradeResult(executed, true, null);
                }
            }
            catch (Exception ex)
            {
                ex.Data.Add("Error occurred in script: ", executedScriptName);
                configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                return new DatabaseUpgradeResult(executed, false, ex);
            }
        }

        /// <summary>
        /// Performs the database downgrade.
        /// </summary>
        /// <param name="scriptToRollback">Script to rollback to but not inlcuding itself or the script to rollback
        /// depending on the multipleRollback flag</param>
        /// <param name="rollbackSuffix">Suffix of the rollback scripts</param>
        /// <param name="multipleRollback">True if you want to rollback all scripts up to the given scriptToRollback
        /// but not including it and false if you just want to rollback the given scriptToRollback and nothing else</param>
        /// <returns></returns>
        public DatabaseUpgradeResult PerformDowngrade()
        {
            var rollbacks = new List<SqlScript>();

            string executedScriptName = null;
            try
            {
                using (configuration.ConnectionManager.OperationStarting(configuration.Log, rollbacks))
                {
                    configuration.Log.WriteInformation("Beginning database downgrade");

                    var scriptsToExecute = GetRollbackScriptsInsideOperation();

                    if (scriptsToExecute == null || scriptsToExecute.Count == 0)
                    {
                        configuration.Log.WriteInformation("No rollback scripts to run or could not match desired downgrades with executed upgrades.");
                        return new DatabaseUpgradeResult(rollbacks, true, null);
                    }

                    configuration.ScriptExecutor.VerifySchema();

                    foreach (var script in scriptsToExecute)
                    {
                        executedScriptName = script.Name;
                        configuration.ScriptExecutor.Execute(script, configuration.Variables);
                        configuration.Journal.RemoveExecutedScript(script);
                        rollbacks.Add(script);
                    }

                    configuration.Log.WriteInformation("Downgrade successful");
                    return new DatabaseUpgradeResult(rollbacks, true, null);
                }
            }
            catch (Exception ex)
            {
                ex.Data.Add("Error occurred in script: ", executedScriptName);
                configuration.Log.WriteError("Downgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                return new DatabaseUpgradeResult(rollbacks, false, ex);
            }
        }

        /// <summary>
        /// Returns a list of scripts that will be executed when the upgrade is performed
        /// </summary>
        /// <returns>The scripts to be executed</returns>
        public List<SqlScript> GetScriptsToExecute()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return GetScriptsToExecuteInsideOperation();
            }
        }

        private List<SqlScript> GetScriptsToExecuteInsideOperation()
        {
            var allScripts = configuration.ScriptProviders.SelectMany(scriptProvider => scriptProvider.GetScripts(configuration.ConnectionManager));
            var executedScripts = configuration.Journal.GetExecutedScripts();

            return allScripts.Where(s => !executedScripts.Any(y => y == s.Name)).ToList();
        }
        
        private List<SqlScript> GetRollbackScriptsInsideOperation()
        {
            var executedScripts = configuration.Journal.GetExecutedScripts();
            return configuration.ScriptProviders
                .SelectMany(scriptProvider => scriptProvider.GetScripts(configuration.ConnectionManager))
                .Where(r => executedScripts.Any(e => r.Name.Equals(e)))
                .ToList();
        }

        public List<string> GetExecutedScripts()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return configuration.Journal.GetExecutedScripts()
                    .ToList();
            }
        }

        ///<summary>
        /// Creates version record for any new migration scripts without executing them.
        /// Useful for bringing development environments into sync with automated environments
        ///</summary>
        ///<returns></returns>
        public DatabaseUpgradeResult MarkAsExecuted()
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.Journal.StoreExecutedScript(script);
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }

        public DatabaseUpgradeResult MarkAsExecuted(string latestScript)
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.Journal.StoreExecutedScript(script);
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                        if (script.Name.Equals(latestScript))
                        {
                            break;
                        }
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }
    }
}