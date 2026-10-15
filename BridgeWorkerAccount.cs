using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

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

    public BridgeConfig()
    {
        PollMs = 1000;
    }
}

public class AccountRequest
{
    public int accountId { get; set; }
    public string customerCode { get; set; }
    public string name { get; set; }
    public string email { get; set; }
    public string phone { get; set; }
    public string address { get; set; }
    public string zipCode { get; set; }
    public string city { get; set; }
    public string country { get; set; }
    public string tariffCategory { get; set; }
    public string source { get; set; }
    public string platform { get; set; }
    public string description { get; set; }
}

public class AccountResult
{
    public bool success { get; set; }
    public int accountId { get; set; }
    public string sageCustomerCode { get; set; }
    public string source { get; set; }
    public string platform { get; set; }
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
        var configPath = Path.GetFullPath(
            Path.Combine(baseDir, "..\\config\\config.accounts.json")
        );

        if (args.Length >= 2 && args[0] == "--config")
            configPath = args[1];

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine("Config introuvable: " + configPath);
            return 1;
        }

        var cfg = Json.Deserialize<BridgeConfig>(
            File.ReadAllText(configPath, Encoding.UTF8)
        );

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
        if (files.Length == 0)
            return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var inFile = files[0];
        var fileName = Path.GetFileName(inFile);
        var processingFile = Path.Combine(cfg.ProcessingDir, fileName);

        try
        {
            File.Move(inFile, processingFile);
        }
        catch
        {
            return;
        }

        Log(cfg, "Traitement: " + fileName);

        var result = new AccountResult();

        try
        {
            var json = File.ReadAllText(processingFile, Encoding.UTF8);
            var req = Json.Deserialize<AccountRequest>(json);

            Validate(req);

            result.accountId = req.accountId;
            result.source = req.source;
            result.platform = req.platform;
            result.sageCustomerCode = CreateAccountInSage(cfg, req);
            result.success = true;
            result.message = "Compte créé dans Sage";
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = "Erreur création compte";
            result.error = ex.ToString();
            Log(cfg, "ERREUR " + fileName + ": " + ex.Message);
        }

        WriteResult(cfg, fileName, result);
        MoveProcessedFile(cfg, processingFile, fileName, result.success);

        Log(
            cfg,
            (result.success ? "OK " : "KO ")
            + fileName
            + " -> "
            + (result.sageCustomerCode ?? "")
        );
    }

    static void Validate(AccountRequest req)
    {
        if (req == null)
            throw new Exception("JSON invalide");

        if (string.IsNullOrWhiteSpace(req.customerCode))
            throw new Exception("customerCode manquant");

        if (string.IsNullOrWhiteSpace(req.name))
            throw new Exception("name manquant");

        if (string.IsNullOrWhiteSpace(req.email))
            throw new Exception("email manquant");
    }

    static void WriteResult(
        BridgeConfig cfg,
        string sourceFileName,
        AccountResult result
    )
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
        var resultPath = Path.Combine(
            cfg.ResultsDir,
            baseName + "_result.json"
        );

        if (File.Exists(resultPath))
        {
            resultPath = Path.Combine(
                cfg.ResultsDir,
                baseName
                + "_result_"
                + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                + ".json"
            );
        }

        File.WriteAllText(
            resultPath,
            Json.Serialize(result),
            Encoding.UTF8
        );
    }

    static void MoveProcessedFile(
        BridgeConfig cfg,
        string processingFile,
        string fileName,
        bool success
    )
    {
        var targetDir = success ? cfg.DoneDir : cfg.ErrorDir;
        var finalPath = Path.Combine(targetDir, fileName);

        if (File.Exists(finalPath))
        {
            finalPath = Path.Combine(
                targetDir,
                Path.GetFileNameWithoutExtension(fileName)
                + "_"
                + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                + ".json"
            );
        }

        File.Move(processingFile, finalPath);
    }

    static void Log(BridgeConfig cfg, string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + " | "
            + message;

        Console.WriteLine(line);

        try
        {
            File.AppendAllText(
                cfg.LogFile,
                line + Environment.NewLine,
                Encoding.UTF8
            );
        }
        catch { }
    }

    static string CreateAccountInSage(
        BridgeConfig cfg,
        AccountRequest req
    )
    {
        dynamic stream = null;
        dynamic client = null;

        try
        {
            var type = Type.GetTypeFromProgID(
                "Objets100c.Cial.Stream",
                true
            );

            stream = Activator.CreateInstance(type);
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            // Rend le traitement idempotent en cas de nouvelle lecture du fichier.
            try
            {
                client = stream.CptaApplication.FactoryClient
                    .ReadNumero(req.customerCode);

                if (client != null)
                {
                    string existingCode = client.CT_Num;
                    Log(cfg, "Compte déjà présent dans Sage: " + existingCode);
                    return existingCode;
                }
            }
            catch
            {
                client = null;
            }

            client = stream.CptaApplication.FactoryClient.Create();
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

            if (!string.IsNullOrWhiteSpace(req.description))
            {
                client.CT_Commentaire = req.description.Length > 35
                    ? req.description.Substring(0, 35)
                    : req.description;
            }

            // tariffCategory sera configuré après validation du mapping Sage.
            client.Write();

            string ctNum = client.CT_Num;

            Log(
                cfg,
                "Compte créé: "
                + ctNum
                + " | source="
                + (req.source ?? "")
                + " | platform="
                + (req.platform ?? "")
            );

            return ctNum;
        }
        finally
        {
            try
            {
                if (client != null)
                    System.Runtime.InteropServices.Marshal
                        .FinalReleaseComObject(client);
            }
            catch { }

            try
            {
                if (stream != null)
                    stream.Close();
            }
            catch { }

            try
            {
                if (stream != null)
                    System.Runtime.InteropServices.Marshal
                        .FinalReleaseComObject(stream);
            }
            catch { }
        }
    }
}
