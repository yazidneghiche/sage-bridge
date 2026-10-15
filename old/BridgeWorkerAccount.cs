using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Reflection;

public class BridgeConfig
{
    public string InboxDir { get; set; }
    public string ProcessingDir { get; set; }
    public string DoneDir { get; set; }
    public string ErrorDir { get; set; }
    public string ResultsDir { get; set; }
    public string LogFile { get; set; }

    public string GcmPath { get; set; }
    public string SageUser { get; set; }
    public string SagePassword { get; set; }

    public int PollMs { get; set; }
    public BridgeConfig() {  PollMs = 1000; }
}

public class AccountRequest
{
    public string customerCode { get; set; } // CT_Num ou code client souhaité
    public string name { get; set; }         // CT_Intitule
    public string email { get; set; }
    public string phone { get; set; }
    public string address { get; set; }
    public string zipCode { get; set; }
    public string city { get; set; }
    public string country { get; set; }
    public string tariffCategory { get; set; }
}

public class AccountResult
{
    public bool success { get; set; }
    public string sageCustomerCode { get; set; }
    public string message { get; set; }
    public string error { get; set; }
}

class Program
{
    static JavaScriptSerializer Json = new JavaScriptSerializer();

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(baseDir, "..\\config\\config.json");
        configPath = Path.GetFullPath(configPath);

        if (args.Length >= 2 && args[0] == "--config")
            configPath = args[1];

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine("Config introuvable: " + configPath);
            return 1;
        }

        var cfg = Json.Deserialize<BridgeConfig>(File.ReadAllText(configPath, Encoding.UTF8));
        EnsureDirs(cfg);

        Log(cfg, "BridgeWorkerAccount démarré. Inbox=" + cfg.InboxDir);

        while (true)
        {
            try
            {
                ProcessOne(cfg);
            }
            catch (Exception ex)
            {
                Log(cfg, "ERREUR boucle: " + ex);
            }

            Thread.Sleep(Math.Max(200, cfg.PollMs));
        }
    }

    static void EnsureDirs(BridgeConfig cfg)
    {
        Directory.CreateDirectory(cfg.InboxDir);
        Directory.CreateDirectory(cfg.ProcessingDir);
        Directory.CreateDirectory(cfg.DoneDir);
        Directory.CreateDirectory(cfg.ErrorDir);
        Directory.CreateDirectory(cfg.ResultsDir);

        var logDir = Path.GetDirectoryName(cfg.LogFile);
        if (!string.IsNullOrWhiteSpace(logDir))
            Directory.CreateDirectory(logDir);
    }

    static void ProcessOne(BridgeConfig cfg)
    {
        var files = Directory.GetFiles(cfg.InboxDir, "*.json");
        if (files.Length == 0) return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var inFile = files[0];

        var fileName = Path.GetFileName(inFile);
        var processingFile = Path.Combine(cfg.ProcessingDir, fileName);

        // Prise "atomique" : move inbox -> processing
        try
        {
            File.Move(inFile, processingFile);
        }
        catch
        {
            // un autre process l'a peut-être pris
            return;
        }

        Log(cfg, "Traitement: " + fileName);

        var result = new OrderResult();
        try
        {
            var json = File.ReadAllText(processingFile, Encoding.UTF8);
            var req = Json.Deserialize<AccountRequest>(json);

            if (req == null) throw new Exception("JSON invalide");
            if (string.IsNullOrWhiteSpace(req.name)) throw new Exception("name manquant");
            if (string.IsNullOrWhiteSpace(req.email)) throw new Exception("email manquant");

            var customerCode = CreateAccountInSage(cfg, req);

            result.success = true;
            result.sageCustomerCode = customerCode;
            result.message = "Compte créé dans Sage";
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = "Erreur création compte";
            result.error = ex.ToString();
            Log(cfg, "ERREUR " + fileName + ": " + ex.Message);
        }

        // Écrire résultat
        var resultPath = Path.Combine(cfg.ResultsDir, Path.GetFileNameWithoutExtension(fileName) + "_result.json");
        File.WriteAllText(resultPath, Json.Serialize(result), Encoding.UTF8);

        // Ranger fichier traité
        var targetDir = result.success ? cfg.DoneDir : cfg.ErrorDir;
        var finalPath = Path.Combine(targetDir, fileName);
        if (File.Exists(finalPath))
        {
            finalPath = Path.Combine(targetDir,
                Path.GetFileNameWithoutExtension(fileName) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
        }
        File.Move(processingFile, finalPath);

        Log(cfg, (result.success ? "OK " : "KO ") + fileName + " -> " + (result.sageDocumentNumber ?? ""));
    }

    static void Log(BridgeConfig cfg, string msg)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg;
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(cfg.LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

	static string CreateAccountInSage(BridgeConfig cfg, AccountRequest req)
    {
        dynamic stream = null;

        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd  = cfg.SagePassword;

            stream.Open();

            dynamic client = stream.CptaApplication.FactoryClient.Create();

            client.CT_Num = req.customerCode;
            client.CT_Intitule = req.name;

            if (!string.IsNullOrWhiteSpace(req.email))
                client.CT_EMail = req.email;

            if (!string.IsNullOrWhiteSpace(req.phone))
                client.CT_Telephone = req.phone;

            if (!string.IsNullOrWhiteSpace(req.address))
                client.CT_Adresse = req.address;

            if (!string.IsNullOrWhiteSpace(req.zipCode))
                client.CT_CodePostal = req.zipCode;

            if (!string.IsNullOrWhiteSpace(req.city))
                client.CT_Ville = req.city;

            if (!string.IsNullOrWhiteSpace(req.country))
                client.CT_Pays = req.country;

            client.Write();

            string ctNum = client.CT_Num;

            stream.Close();

            return ctNum;
        }
        finally
        {
            if (stream != null)
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
        }
    }
}