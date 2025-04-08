using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Renci.SshNet;
using CommandLine;

namespace ProxmoxSshTunnel
{
    class Program
    {
        public class Options
        {
            [Option('s', "source", Required = true, HelpText = "Source Proxmox server address")]
            public string SourceServer { get; set; }

            [Option('d', "destination", Required = true, HelpText = "Destination Proxmox server address")]
            public string DestinationServer { get; set; }

            [Option('u', "username", Required = true, HelpText = "SSH username")]
            public string Username { get; set; }

            [Option('p', "password", Required = false, HelpText = "SSH password (optional)")]
            public string Password { get; set; }

            [Option('k', "keyfile", Required = false, HelpText = "Path to SSH private key file")]
            public string KeyFile { get; set; }

            [Option('l', "local-port", Default = 22222, HelpText = "Local port for SSH tunnel")]
            public int LocalPort { get; set; }

            [Option('r', "remote-port", Default = 22, HelpText = "Remote port for SSH tunnel")]
            public int RemotePort { get; set; }
            
            [Option('v', "vm-id", Required = true, HelpText = "VM ID to migrate")]
            public int VmId { get; set; }
            
            [Option("source-node", Required = true, HelpText = "Source Proxmox node name")]
            public string SourceNode { get; set; }
            
            [Option("destination-node", Required = true, HelpText = "Destination Proxmox node name")]
            public string DestinationNode { get; set; }
            
            [Option("token-name", Required = false, HelpText = "API token name (format: user@pam!token)")]
            public string TokenName { get; set; }
            
            [Option("token-value", Required = false, HelpText = "API token value")]
            public string TokenValue { get; set; }
        }

        static async Task Main(string[] args)
        {
            await Parser.Default.ParseArguments<Options>(args)
                .WithParsedAsync<Options>(RunWithOptions);
        }

        static async Task RunWithOptions(Options options)
        {
            Console.WriteLine("Starting Proxmox SSH tunnel for VM migration...");
            
            // Validate required parameters
            if (string.IsNullOrEmpty(options.Password) && string.IsNullOrEmpty(options.KeyFile))
            {
                Console.WriteLine("Error: Either password or key file must be provided.");
                return;
            }

            try
            {
                // Create authentication method
                AuthenticationMethod authMethod;
                if (!string.IsNullOrEmpty(options.KeyFile))
                {
                    var keyFile = new PrivateKeyFile(options.KeyFile);
                    authMethod = new PrivateKeyAuthenticationMethod(options.Username, keyFile);
                    Console.WriteLine($"Using key file authentication: {options.KeyFile}");
                }
                else
                {
                    authMethod = new PasswordAuthenticationMethod(options.Username, options.Password);
                    Console.WriteLine("Using password authentication");
                }

                // Set up connection info for source server, does not utilizie loopback for reconnect 
                var connectionInfo = new ConnectionInfo(
                    options.SourceServer,
                    options.RemotePort,
                    options.Username,
                    authMethod
                );

                // Create and start the SSH tunnel
                //can also use scp if rewritten
                using (var client = new SshClient(connectionInfo))
                {
                    Console.WriteLine($"Connecting to source server {options.SourceServer}...");
                    client.Connect();
                    Console.WriteLine("Connected successfully.");

                    // Forward the SSH port from local to destination server
                    // this shouldn't need a router set ssh tunnel
                    var forwardedPort = new ForwardedPortLocal(
                        "127.0.0.1",
                        (uint)options.LocalPort,
                        options.DestinationServer,
                        (uint)options.RemotePort
                    );
// loopback ip address 
                    Console.WriteLine($"Setting up SSH tunnel: localhost:{options.LocalPort} -> {options.DestinationServer}:{options.RemotePort}");
                    client.AddForwardedPort(forwardedPort);
                    forwardedPort.Start();
                    Console.WriteLine("SSH tunnel established successfully.");

                    // Execute Proxmox migration command
                    await MigrateVm(options, client);

                    Console.WriteLine("Keeping tunnel open. Press Enter to close the connection...");
                    Console.ReadLine();

                    // Clean up
                    forwardedPort.Stop();
                    client.Disconnect();
                    Console.WriteLine("SSH tunnel closed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }

        static async Task MigrateVm(Options options, SshClient client)
        {
            try
            {
                Console.WriteLine($"Starting migration of VM {options.VmId} from {options.SourceNode} to {options.DestinationNode}...");

                string authPart = string.Empty;
                if (!string.IsNullOrEmpty(options.TokenName) && !string.IsNullOrEmpty(options.TokenValue))
                {
                    authPart = $"-H \"Authorization: PVEAPIToken={options.TokenName}={options.TokenValue}\"";
                }

                // Create the migration command - adjust as needed based on your Proxmox setup
                string migrateCommand = 
                    $"pvesh create /nodes/{options.SourceNode}/qemu/{options.VmId}/migrate " +
                    $"--target {options.DestinationNode} " +
                    $"--online 1 " +
                    $"--with-local-disks 1 " +
                    $"{authPart}";

                using (var command = client.CreateCommand(migrateCommand))
                {
                    Console.WriteLine("Executing migration command...");
                    string result = command.Execute();
                    Console.WriteLine("Migration command output:");
                    Console.WriteLine(result);
                    
                    if (command.ExitStatus != 0)
                    {
                        Console.WriteLine($"Migration command failed with exit code: {command.ExitStatus}");
                        Console.WriteLine($"Error output: {command.Error}");
                    }
                    else
                    {
                        Console.WriteLine("Migration command executed successfully.");
                        
                        // Monitor migration status
                        await MonitorMigrationStatus(options, client);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Migration error: {ex.Message}");
            }
        }

        static async Task MonitorMigrationStatus(Options options, SshClient client)
        {
            Console.WriteLine("Monitoring migration status...");
            bool migrationCompleted = false;
            int attempts = 0;
            
            string authPart = string.Empty;
            if (!string.IsNullOrEmpty(options.TokenName) && !string.IsNullOrEmpty(options.TokenValue))
            {
                authPart = $"-H \"Authorization: PVEAPIToken={options.TokenName}={options.TokenValue}\"";
            }

            while (!migrationCompleted && attempts < 30)  // Timeout after 30 attempts, increase this up to 100
            {
                string statusCommand = 
                    $"pvesh get /nodes/{options.SourceNode}/qemu/{options.VmId}/status/current " +
                    $"{authPart} -output-format json";
                
                using (var command = client.CreateCommand(statusCommand))
                {
                    string result = command.Execute();
                    
                    // Simple check - in a real application, you'd want to parse json for data check
                    if (result.Contains("\"status\":\"stopped\"") || result.Contains("not found"))
                    {
                        Console.WriteLine("VM appears to have been migrated (no longer on source node).");
                        migrationCompleted = true;
                    }
                    else
                    {
                        Console.WriteLine($"Migration in progress... Attempt {++attempts}");
                        await Task.Delay(10000);  // Check every 10 seconds if successfull
                    }
                }
            }
            
            if (!migrationCompleted)
            {
                Console.WriteLine("Migration monitoring timed out. Please check manually.");
            }
            else
            {
                Console.WriteLine("VM migration completed successfully!");
            }
        }
    }
}
