using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Net.Mail;
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
    // nouvel ajout : nom et prénom séparés dans le JSON.
    public string firstname { get; set; }
    public string lastname { get; set; }
    public string email { get; set; }
    public string phone { get; set; }
    public string address { get; set; }
    public string addressComplement { get; set; }
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
    // nouvel ajout : distinguer création, compte existant et étape en erreur.
    public bool created { get; set; }
    public bool alreadyExists { get; set; }
    public bool temporaryCustomerCode { get; set; }
    public string stage { get; set; }
    public string processedAt { get; set; }
}

class Program
{
    static JavaScriptSerializer Json = new JavaScriptSerializer();

    [STAThread]
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

        BridgeConfig cfg;
        try
        {
            cfg = Json.Deserialize<BridgeConfig>(
                File.ReadAllText(configPath, Encoding.UTF8)
            );
            ValidateConfig(cfg);
            EnsureDirs(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERREUR configuration: " + ex.Message);
            return 1;
        }

        Log(cfg, "BridgeWorkerAccount démarré. Inbox=" + cfg.InboxDir);

        while (true)
        {
            try
            {
                ProcessOne(cfg);
            }
            catch (Exception ex)
            {
                // Ne pas continuer si l’écriture du résultat ou l’archivage échoue
                // après une éventuelle création Sage. Le JSON reste dans processing.
                Log(cfg, "ERREUR infrastructure, arrêt du worker: " + ex);
                return 1;
            }

            Thread.Sleep(Math.Max(200, cfg.PollMs));
        }
    }

    static void ValidateConfig(BridgeConfig cfg)
    {
        if (cfg == null) throw new Exception("Configuration JSON vide.");
        string[] dirs = { cfg.InboxDir, cfg.ProcessingDir, cfg.DoneDir, cfg.ErrorDir, cfg.ResultsDir };
        for (int i = 0; i < dirs.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(dirs[i]) || !Path.IsPathRooted(dirs[i]))
                throw new Exception("Les cinq dossiers doivent être renseignés avec des chemins absolus.");
            string current = Path.GetFullPath(dirs[i]).TrimEnd('\\', '/');
            for (int j = 0; j < i; j++)
            {
                string other = Path.GetFullPath(dirs[j]).TrimEnd('\\', '/');
                if (string.Equals(current, other, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Les dossiers inbox, processing, done, error et results doivent être distincts.");
            }
        }
        if (string.IsNullOrWhiteSpace(cfg.LogFile) || !Path.IsPathRooted(cfg.LogFile))
            throw new Exception("LogFile doit être un chemin absolu.");
        if (string.IsNullOrWhiteSpace(cfg.GcmPath) || !File.Exists(cfg.GcmPath))
            throw new Exception("Fichier GcmPath introuvable.");
        if (string.IsNullOrWhiteSpace(cfg.SageUser))
            throw new Exception("SageUser manquant.");
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
        result.stage = "lecture_json";

        try
        {
            var json = File.ReadAllText(processingFile, Encoding.UTF8);
            var req = Json.Deserialize<AccountRequest>(json);

            // Conserver la corrélation même en cas de validation incorrecte.
            if (req != null)
            {
                result.accountId = req.accountId;
                result.source = req.source;
                result.platform = req.platform;
            }

            result.stage = "validation";
            Validate(req);
            result.temporaryCustomerCode = req.customerCode.EndsWith(
                "tmp", StringComparison.OrdinalIgnoreCase
            );

            result.sageCustomerCode = CreateAccountInSage(cfg, req, result);
            result.success = true;
            result.stage = "termine";
            result.message = result.created
                ? "Compte créé dans Sage"
                : "Compte déjà présent dans Sage, aucune modification";
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = "Erreur traitement compte à l’étape " + result.stage;
            result.error = ex.ToString();
            Log(cfg, "ERREUR " + fileName + " | étape=" + result.stage + " | " + ex);
        }

        result.processedAt = DateTime.UtcNow.ToString("o");
        WriteResult(cfg, fileName, result);
        MoveProcessedFile(cfg, processingFile, fileName, result.success);

        Log(cfg, (result.success ? "OK " : "KO ") + fileName
            + " | accountId=" + result.accountId
            + " | source=" + SafeLog(result.source)
            + " | platform=" + SafeLog(result.platform)
            + " | code=" + SafeLog(result.sageCustomerCode)
            + " | " + result.message);
    }

    static void Validate(AccountRequest req)
    {
        if (req == null) throw new Exception("JSON invalide.");
        if (req.accountId <= 0) throw new Exception("accountId doit être un entier positif.");

        req.customerCode = CheckText(req.customerCode, "customerCode", 17, true);
        req.firstname = CheckText(req.firstname, "firstname", 35, false);
        req.lastname = CheckText(req.lastname, "lastname", 35, false);
        string contact = ((req.firstname ?? "") + " " + (req.lastname ?? "")).Trim();
        CheckText(contact, "firstname + lastname (CT_Contact)", 35, false);

        // Pour un particulier sans société, utiliser le prénom et le nom.
        if (string.IsNullOrWhiteSpace(req.name)) req.name = contact;
        req.name = CheckText(req.name, "name", 69, true);
        req.email = CheckText(req.email, "email", 69, true);
        try
        {
            var parsed = new MailAddress(req.email);
            if (!string.Equals(parsed.Address, req.email, StringComparison.OrdinalIgnoreCase))
                throw new Exception();
        }
        catch { throw new Exception("Adresse email invalide."); }

        req.phone = CheckText(req.phone, "phone", 21, false);
        req.address = CheckText(req.address, "address", 35, false);
        req.addressComplement = CheckText(req.addressComplement, "addressComplement", 35, false);
        req.zipCode = CheckText(req.zipCode, "zipCode", 9, false);
        req.city = CheckText(req.city, "city", 35, false);
        req.country = CheckText(req.country, "country", 35, false);
        req.description = CheckText(req.description, "description (CT_Commentaire)", 35, false);
        req.source = CheckText(req.source, "source", 64, true);
        req.platform = CheckText(req.platform, "platform", 64, true);
    }

    static string CheckText(string value, string field, int max, bool required)
    {
        value = value == null ? null : value.Trim();
        if (required && string.IsNullOrWhiteSpace(value))
            throw new Exception(field + " manquant.");
        if (value != null && value.Length > max)
            throw new Exception(field + " trop long : " + value.Length + " caractères, maximum " + max + ". Aucune troncature effectuée.");
        return value;
    }

    static string SafeLog(string value)
    {
        return (value ?? "").Replace("\r", " ").Replace("\n", " ");
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
            resultPath = Path.Combine(cfg.ResultsDir,
                baseName + "_result_" + Guid.NewGuid().ToString("N") + ".json");
        }

        var temporaryPath = resultPath + ".tmp";
        File.WriteAllText(temporaryPath, Json.Serialize(result), Encoding.UTF8);
        File.Move(temporaryPath, resultPath);
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
                + Guid.NewGuid().ToString("N")
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
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERREUR écriture log: " + ex.Message);
        }
    }

    static string CreateAccountInSage(
        BridgeConfig cfg,
        AccountRequest req,
        AccountResult result
    )
    {
        dynamic stream = null;
        dynamic client = null;
        dynamic cpta = null;
        dynamic factory = null;

        try
        {
            result.stage = "ouverture_sage";
            var type = Type.GetTypeFromProgID(
                "Objets100c.Cial.Stream",
                true
            );

            stream = Activator.CreateInstance(type);
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            cpta = stream.CptaApplication;
            factory = cpta.FactoryClient;

            result.stage = "recherche_client";
            // Une erreur Sage doit remonter, pas être prise pour un compte absent.
            if ((bool)factory.ExistNumero(req.customerCode))
            {
                client = factory.ReadNumero(req.customerCode);
                string existingCode = client.CT_Num;
                string existingEmail = Convert.ToString(client.CT_EMail);
                if (!string.Equals((existingEmail ?? "").Trim(), req.email, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Ce numéro client existe avec un autre email. Aucune modification effectuée.");

                result.created = false;
                result.alreadyExists = true;
                Log(cfg, "Compte déjà présent dans Sage: " + SafeLog(existingCode));
                return existingCode;
            }

            result.stage = "creation_client";
            client = factory.Create();
            client.CT_Num = req.customerCode;
            client.CT_Intitule = req.name;

            // nouvel ajout : contact principal, prénom puis nom.
            string contact = ((req.firstname ?? "") + " " + (req.lastname ?? "")).Trim();
            if (!string.IsNullOrWhiteSpace(contact))
            {
                result.stage = "champ_CT_Contact";
                client.CT_Contact = contact;
            }

            if (!string.IsNullOrWhiteSpace(req.email))
            {
                result.stage = "champ_CT_EMail";
                client.CT_EMail = req.email;
            }

            if (!string.IsNullOrWhiteSpace(req.phone))
            {
                result.stage = "champ_CT_Telephone";
                client.CT_Telephone = req.phone;
            }

            if (!string.IsNullOrWhiteSpace(req.address))
            {
                result.stage = "champ_CT_Adresse";
                client.CT_Adresse = req.address;
            }

            if (!string.IsNullOrWhiteSpace(req.addressComplement))
            {
                result.stage = "champ_CT_Complement";
                client.CT_Complement = req.addressComplement;
            }

            if (!string.IsNullOrWhiteSpace(req.zipCode))
            {
                result.stage = "champ_CT_CodePostal";
                client.CT_CodePostal = req.zipCode;
            }

            if (!string.IsNullOrWhiteSpace(req.city))
            {
                result.stage = "champ_CT_Ville";
                client.CT_Ville = req.city;
            }

            if (!string.IsNullOrWhiteSpace(req.country))
            {
                result.stage = "champ_CT_Pays";
                client.CT_Pays = req.country;
            }

            if (!string.IsNullOrWhiteSpace(req.description))
            {
                result.stage = "champ_CT_Commentaire";
                client.CT_Commentaire = req.description;
            }

            // tariffCategory sera configuré après validation du mapping Sage.
            result.stage = "ecriture_client";
            client.Write();
            result.created = true;

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

            ReleaseCom(factory);
            ReleaseCom(cpta);

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
    static void ReleaseCom(object value)
    {
        try
        {
            if (value != null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }
}
