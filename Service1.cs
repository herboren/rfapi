using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;

namespace rfapi
{
    public partial class Service1 : ServiceBase
    {
        Dictionary<string, int> faxErrorCount = new Dictionary<string, int>();
        
        // Timers for event firing
        private Timer timerPruneCheck;
        private Timer timerErrorCheck;
        private Timer timerCacheCheck;

        // <!-- Directories to monitor-->
        private string rightFaxDropDirectory = string.Empty;
        private string rightFaxCacheDirectory = string.Empty;

        // <!-- Pruning-->
        private string rightFaxDropPruneInterval = string.Empty;
        private string rightFaxDropFileAgeHours = string.Empty;

        // <!-- Error Check, Move, Rename -->
        private string rightFaxDropErrorInterval = string.Empty;
        private string rightFaxDropMaxErrorChecks = string.Empty;

        // <!--Error file age check-->
        private string rightFaxCacheInterval = string.Empty;
        private string errorCacheDaysToKeep = string.Empty;    
        //
        private int maxErrorThreshold = 0;

        /// <summary>
        /// Read config and set service name;
        /// </summary>
        public Service1()
        {
            InitializeComponent();
            InitializeConfiguration();            
        }

        // EventID per event, identifying events in EventViewer
        public enum RFAPIEVENTID : int
        {            
            Config = 0,            
            Renamed = 1,
            Cached = 2,
            Deleted = 3,
            Error = 4
        }
        // Override the OnStart method to perform startup tasks
        protected override void OnStart(string[] args)
        {
            timerPruneCheck = new Timer(MakeHoursMilliseconds(double.Parse(rightFaxDropPruneInterval))); // When to recheck for files in drop
            timerErrorCheck = new Timer(MakeMinutes(double.Parse(rightFaxDropErrorInterval))); // When to check for errors in drop
            timerCacheCheck = new Timer(MakeHoursMilliseconds(double.Parse(rightFaxCacheInterval))); // When to check eror files age in ercache

            // Prune Checking Event
            timerPruneCheck.AutoReset = true;
            timerPruneCheck.Elapsed += timerPruneCheck_Elapsed;
            timerPruneCheck.Start();

            // Error Checking Event
            timerErrorCheck.AutoReset = true;
            timerErrorCheck.Elapsed += timerErrorCheck_Elapsed;
            timerErrorCheck.Start();

            // Prune Checking Event
            timerCacheCheck.AutoReset = true;
            timerCacheCheck.Elapsed += timerCacheCheck_Elapsed;
            timerCacheCheck.Start();
        }

        private double MakeHoursMilliseconds(double val)
        {
            int s_hours = 3600;
            return (val * s_hours) * 1000;

        }
        private double MakeMinutes(double val)
        {
            int s_minutes = 60;
            return (val * s_minutes) * 1000;
        }
        /// <summary>
        /// Scan Drop folder for extnesionless files on Event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void timerPruneCheck_Elapsed(object sender, ElapsedEventArgs e)
        {
            foreach (var filePath in Directory.GetFiles(rightFaxDropDirectory))
            {
                ProcessFile(filePath);
            }
        }

        /// <summary>
        /// Scan Drop folder for error files on Event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void timerErrorCheck_Elapsed(object sender, ElapsedEventArgs e)
        {
            foreach (var filePath in Directory.GetFiles(rightFaxDropDirectory))
            {
                ErrorCheckReProcessFile(filePath);
            }
        }

        /// <summary>
        /// Scan Cache folder for error file ages on Event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>     
        private void timerCacheCheck_Elapsed(object sender, ElapsedEventArgs e)
        {
            foreach (var filePath in Directory.GetFiles(rightFaxCacheDirectory))
            {
                ErrorCacheProcessFile(filePath);
            }
        }

        /// <summary>
        /// Get the file info of the file within the drop folder. 
        /// Status check the last modified since is was last modified
        /// Remember status is reset every modification
        /// </summary>
        /// <param name="filePath"></param>
        ///         
        private void ProcessFile(string filePath)
        {
            if (!Path.GetExtension(filePath).ToLower().StartsWith(".eml") &&
                !Path.GetExtension(filePath).ToLower().StartsWith(".bak") &&
                !Path.GetExtension(filePath).ToLower().StartsWith(".error"))
            {
                DateTime lastModified = File.GetLastWriteTime(filePath);
                TimeSpan untouched = TimeSpan.FromHours(double.Parse(rightFaxDropFileAgeHours)); // From Hours

                if (DateTime.Now - lastModified > untouched)
                {
                    try
                    {
                        // Check all existing files in the directory
                        EventLog.WriteEntry("rfapi", $"[Automated Process - Removed File]\nFile: " +
                            $"{Path.GetFileName(filePath)}\n\n[Action]\nFax trasmission DELETED.\n{GetSenderEmail(filePath)}",
                            EventLogEntryType.Warning, (int)RFAPIEVENTID.Deleted);

                        File.Delete(filePath);
                    }
                    catch (Exception ex)
                    {
                        EventLog.WriteEntry("rfapi", $"Error processing file: {filePath}.\n{ex.Message}",
                            EventLogEntryType.Error, (int)RFAPIEVENTID.Error);
                    }
                }
            }
        } // WORKING! DO NOT TOUCH!

        /// The dictionary can accommodate numerous filename-count pairs, as each fax name is unique.
        /// Only one unique name is retained, and it is periodically checked after a specified interval for success or failure.
        /// If successful, the filename will no longer be present.
        /// </summary>
        /// <param name="filename"></param>
        /// 
        private void ErrorCheckReProcessFile(string filePath)
        {            
            int.TryParse(rightFaxDropMaxErrorChecks, out maxErrorThreshold);
            string filename = Path.GetFileName(filePath);
            if (Path.GetExtension(filePath).StartsWith(".error"))
            {                
                // Make sure filename isn't already stored
                if (!faxErrorCount.ContainsKey(filename))
                {
                    faxErrorCount.Add(filename, 1);                    
                    EventLog.WriteEntry("rfapi", $"[Automated Process - Renamed File]\n{filename} {(char)'\u21d2'}" +
                        $" {Path.ChangeExtension(filename, ".eml")}\n\n[Action]\nERROR file renamed to EML.\nAttempt: 1\n" +
                        $"{GetSenderEmail(filePath)}", EventLogEntryType.Warning, (int)RFAPIEVENTID.Renamed);
                    File.Move(filePath, Path.ChangeExtension(filePath, ".eml"));
                }
                else
                {
                    int errorCount;
                    if (faxErrorCount.TryGetValue(filename, out errorCount))
                    {
                        if (errorCount < maxErrorThreshold)
                        {
                            // Increment each time file is identified
                            faxErrorCount[filename] += 1;
                            EventLog.WriteEntry("rfapi", $"[Automated Process - Renamed File]\n{filename} {(char)'\u21d2'} " +
                                $"{Path.ChangeExtension(filename, ".eml")}\n\n[Action]\nERROR file renamed to EML.\nAttempts:" +
                                $" {faxErrorCount[filename]} time(s)\n{GetSenderEmail(filePath)}", EventLogEntryType.Warning, 
                                (int)RFAPIEVENTID.Renamed);
                            File.Move(filePath, Path.ChangeExtension(filePath, ".eml"));

                        }
                        else
                        {
                            // Mmove to cache after 3 retries
                            EventLog.WriteEntry("rfapi", $"[Automated Process - Moved File]\n{filename} {(char)'\u21d2'} " +
                                $"{Path.Combine(rightFaxCacheDirectory, Path.GetFileName(filePath))}\n\n[Action]\n" +
                                $"Fax trasmission DELETED. Max ERROR rename attempts met.\nAttempts:" +
                                $" {faxErrorCount[filename]} time(s)\n{GetSenderEmail(filePath)}", EventLogEntryType.Warning, 
                                (int)RFAPIEVENTID.Cached);
                            File.Move(filePath, Path.Combine(rightFaxCacheDirectory, Path.GetFileName(filePath)));
                            faxErrorCount.Remove(filename);
                        }
                    }
                }                    
            }
        } // WORKING! DO NOT TOUCH!

        private void ErrorCacheProcessFile(string filePath)
        {            
            if (Path.GetExtension(filePath).ToLower().StartsWith(".error"))
            {
                DateTime lastCreated = File.GetCreationTime(filePath);
                TimeSpan untouched = TimeSpan.FromDays(double.Parse(errorCacheDaysToKeep)); // From Days

                if (DateTime.Now - lastCreated > untouched)
                {
                    string filename = Path.GetFileName(filePath);
                    // Mmove to cache after 3 retries
                    EventLog.WriteEntry("rfapi", $"[Automated Process - Removed File]\n{filename} {(char)'\u21d2'}" +
                        $" {{DELETED}}\n\n[Action]\nERROR trasmission DELETED. Age of file exceeds > {errorCacheDaysToKeep}" +
                        $" days\n{GetSenderEmail(filePath)}", EventLogEntryType.Warning, (int)RFAPIEVENTID.Deleted);
                    File.Delete(filePath);
                }
            }
        }

        /// <summary>
        /// Initialize COnfiguration for drop monitoring
        /// </summary>
        private void InitializeConfiguration()
        {
            // <!-- Directories to monitor-->
            rightFaxDropDirectory = ConfigurationManager.AppSettings["rightFaxDropDirectory"];
            rightFaxCacheDirectory = ConfigurationManager.AppSettings["rightFaxCacheDirectory"];
            // <!-- Pruning-->
            rightFaxDropPruneInterval = ConfigurationManager.AppSettings["rightFaxDropPruneInterval"];
            rightFaxDropFileAgeHours = ConfigurationManager.AppSettings["rightFaxDropFileAgeHours"];
            // < !--Error Check, Move, Rename-- >
            rightFaxDropErrorInterval = ConfigurationManager.AppSettings["rightFaxDropErrorInterval"];
            rightFaxDropMaxErrorChecks = ConfigurationManager.AppSettings["rightFaxDropMaxErrorChecks"];
            // < !--Error file age check-- >
            rightFaxCacheInterval = ConfigurationManager.AppSettings["rightFaxCacheInterval"];
            errorCacheDaysToKeep = ConfigurationManager.AppSettings["errorCacheDaysToKeep"];
            // Timer config

            // Log configuration in event log
            EventLog.WriteEntry("rfapi", $"[RFAPI Configutation]\n" +
                                         $"rightFaxDropDirectory: {rightFaxDropDirectory}\n" +
                                         $"rightFaxCacheDirectory: {rightFaxCacheDirectory}\n" +                                         
                                         $"rightFaxDropPruneInterval: {rightFaxDropPruneInterval}\n" +
                                         $"rightFaxDropFileAgeHours: {rightFaxDropFileAgeHours}\n" +
                                         $"rightFaxDropErrorInterval: {rightFaxDropErrorInterval}\n" +
                                         $"rightFaxDropMaxErrorChecks: {rightFaxDropMaxErrorChecks}\n" +
                                         $"rightFaxCacheInterval: {rightFaxCacheInterval}\n" +
                                         $"errorCacheDaysToKeep: {errorCacheDaysToKeep}\n\n" +                                       
                                         $"[Event IDs]\n" +                                       
                                         $"Config: 0\n" +
                                         $"Renamed: 1\n" +
                                         $"Cached: 2\n" +
                                         $"Deleted: 3\n" +
                                         $"Error: 4\n",                                
                                         EventLogEntryType.Information, (int)RFAPIEVENTID.Config);
        }


        /// <summary>
        /// Identify users transmission in fax that caused lockup.
        /// Log it to event viewer.
        /// </summary>
        /// <param name="fileContents"></param>
        /// <returns></returns>
        private string GetSenderEmail(string fileContents)
        {
            try
            {
                StringBuilder stringBuilder = new StringBuilder();
                string[] lines = File.ReadAllLines(fileContents);

                foreach (var line in lines)
                {
                    if (line.StartsWith("x-sender"))
                    {
                        stringBuilder.Append($"\n[Owner Transmission]\nSender: {line.Split(':')[1].Trim()} {(char)'\u21d2'} ");
                    }
                    if (line.StartsWith(""))
                    {
                        stringBuilder.Append($"Receiver: {line.Split(':')[1].Trim()}\n");
                    }
                }
                return stringBuilder.ToString();
            }
            catch (Exception ex)
            {
                // Log any exceptions
                EventLog.WriteEntry("rfapi", $"Error opening file, could not get user fax info.\n{ex.Message}", 
                    EventLogEntryType.Error, (int)RFAPIEVENTID.Error);
            }
            return "Could not open file to identify Sender/Receiver\n";
        }

        // Override the OnStop method to perform cleanup tasks
        protected override void OnStop()
        {
            // dropWatch.EnableRaisingEvents = false;
            // dropWatch.Dispose();
            timerPruneCheck.Stop();
            timerErrorCheck.Stop();
            timerCacheCheck.Stop();
            faxErrorCount.Clear();
        }
    }
} 