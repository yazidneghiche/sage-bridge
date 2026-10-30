using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

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

public class OrderRequest
{
    public int? type { get; set; } // API : 0 devis, 1 commande, 2 facture.
    public string customerCode { get; set; }
    public string date { get; set; } // yyyy-MM-dd
    public string order_reference { get; set; }
    public string reference { get; set; } // ajout
    public string contact { get; set; }   //
    public string warehouse { get; set; }
    public List<OrderLine> lines { get; set; }
}

public class OrderLine
{
    public string reference { get; set; }
    public decimal quantity { get; set; }
    public decimal? unitPrice { get; set; }
}

public class OrderResult
{
    public int? requestedType { get; set; }
    public string lastSageDocumentNumber { get; set; }
    public int? lastSageDocumentType { get; set; }
    public bool success { get; set; }
    public string sageDocumentNumber { get; set; }
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

        Log(cfg, "BridgeWorker démarré. Inbox=" + cfg.InboxDir);

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
            var req = Json.Deserialize<OrderRequest>(json);

            ValidateRequest(req);
            result.requestedType = req.type;
            var docNo = CreateOrderInSage(cfg, req, result);

            result.success = true;
            result.sageDocumentNumber = docNo;
            result.message = DocumentLabel(req.type.Value) + " créé(e)";
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = "Erreur création document : vérifier Sage avant toute relance";
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

    // Convention API distincte de DocumentType Sage.
    static int GetInitialSageType(int type)
    {
        switch (type)
        {
            case 0: return 0; // Devis.
            case 1: return 0; // Parcours existant : devis puis Commander.
            case 2: return 0; // Devis, puis commande, livraison et facture.
            default: throw new ArgumentException("Type attendu : 0, 1 ou 2.");
        }
    }

    static string DocumentLabel(int type)
    {
        switch (type)
        {
            case 0: return "Devis";
            case 1: return "Commande";
            case 2: return "Facture";
            default: throw new ArgumentException("Type inconnu.");
        }
    }

    static DateTime RequestDate(OrderRequest req)
    {
        DateTime date;
        if (!DateTime.TryParseExact(req.date, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            throw new ArgumentException("date obligatoire au format yyyy-MM-dd.");
        return date;
    }

    static void ValidateRequest(OrderRequest req)
    {
        if (req == null) throw new ArgumentException("JSON invalide.");
        if (!req.type.HasValue)
            throw new ArgumentException("type manquant : 0 devis, 1 commande, 2 facture.");
        GetInitialSageType(req.type.Value);
        if (string.IsNullOrWhiteSpace(req.customerCode))
            throw new ArgumentException("customerCode manquant.");
        if (string.IsNullOrWhiteSpace(req.order_reference))
            throw new ArgumentException("order_reference manquant.");
        RequestDate(req);
        if (req.lines == null || req.lines.Count == 0)
            throw new ArgumentException("lines vide.");
        foreach (var line in req.lines)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.reference))
                throw new ArgumentException("Référence article manquante.");
            if (line.quantity <= 0)
                throw new ArgumentException("Quantité positive obligatoire : " + line.reference);
            if (line.unitPrice.HasValue && line.unitPrice.Value < 0)
                throw new ArgumentException("Prix négatif interdit : " + line.reference);
        }
    }

    static void ReleaseCom(object value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }

    static string CreateOrderInSage(BridgeConfig cfg, OrderRequest req, OrderResult result)
    {
        dynamic stream = null;
        dynamic client = null;
        dynamic doc = null;
        string piece;
        int requestedType = req.type.Value;
        DateTime documentDate = RequestDate(req);

        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            client = stream.CptaApplication.FactoryClient.ReadNumero(req.customerCode);
            doc = stream.FactoryDocumentVente.CreateType(GetInitialSageType(requestedType));
            doc.SetDefaultClient(client);
            doc.DO_Date = documentDate;

            // Préparer le devis pour les transformations, sauf si on le conserve.
            if (requestedType != 0)
                doc.DO_Statut = 2;

            doc.DO_RefExterne = req.order_reference;
            doc.DO_Ref = req.reference;
            if (!string.IsNullOrEmpty(req.contact))
                doc.DO_Contact = req.contact;

            Log(cfg, "Création devis, destination " + DocumentLabel(requestedType)
                + " pour client : " + req.customerCode);
            doc.Write();
            result.lastSageDocumentNumber = doc.DO_Piece;
            result.lastSageDocumentType = 0;
            Log(cfg, "Entête Sage créée : " + doc.DO_Piece
                + " ; référence API : " + req.order_reference);

            foreach (var line in req.lines)
            {
                dynamic article = null;
                dynamic lig = null;
                try
                {
                    article = stream.FactoryArticle.ReadReference(line.reference);
                    lig = doc.FactoryDocumentLigne.Create();
                    lig.SetDefaultArticle(article, (double)line.quantity);
                    if (line.unitPrice.HasValue)
                        lig.DL_PrixUnitaire = (double)line.unitPrice.Value;
                    lig.Write();
                }
                finally
                {
                    ReleaseCom((object)lig);
                    ReleaseCom((object)article);
                }
            }
            piece = doc.DO_Piece;
        }
        finally
        {
            ReleaseCom((object)doc);
            ReleaseCom((object)client);
            try { if (stream != null) stream.Close(); } catch { }
            ReleaseCom((object)stream);
        }

        // Arrêter le parcours au type demandé.
        if (requestedType == 0)
            return piece;

        piece = TransformDocumentInSage(cfg, piece, 0, 1, documentDate);
        result.lastSageDocumentNumber = piece;
        result.lastSageDocumentType = 1;
        if (requestedType == 1)
            return piece;

        piece = TransformDocumentInSage(cfg, piece, 1, 3, documentDate);
        result.lastSageDocumentNumber = piece;
        result.lastSageDocumentType = 3;

        piece = TransformDocumentInSage(cfg, piece, 3, 6, documentDate);
        result.lastSageDocumentNumber = piece;
        result.lastSageDocumentType = 6;
        return piece;
    }

    static string TransformDocumentInSage(
        BridgeConfig cfg, string sourcePiece, int sourceType,
        int targetType, DateTime documentDate)
    {
        dynamic stream = null;
        dynamic source = null;
        dynamic process = null;
        dynamic target = null;
        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            source = stream.FactoryDocumentVente.ReadPiece(sourceType, sourcePiece);
            // Utiliser les transformations Sage, sans modifier DO_Type.
            switch (targetType)
            {
                case 1:
                    process = stream.Transformation.Vente.CreateProcess_Commander();
                    break;
                case 3:
                    process = stream.Transformation.Vente.CreateProcess_Livrer();
                    break;
                case 6:
                    process = stream.Transformation.Vente.CreateProcess_Facturer();
                    break;
                default:
                    throw new ArgumentException("Transformation non prise en charge.");
            }

            process.AddDocument(source);
            try { process.DO_Date = documentDate; } catch { }

            if (!process.CanProcess)
            {
                string message = "Transformation impossible depuis " + sourcePiece;
                for (int i = 1; i <= process.Errors.Count; i++)
                {
                    dynamic error = process.Errors.Item(i);
                    try { message += " [" + error.ErrorCode + "] " + error.Text; }
                    finally { ReleaseCom((object)error); }
                }
                throw new Exception(message);
            }

            process.Process();
            // Ne pas ignorer des documents supplémentaires créés par Sage.
            if (process.ListDocumentsResult.Count != 1)
                throw new Exception("Nombre de documents résultat inattendu après "
                    + sourcePiece + " : contrôler Sage avant toute relance.");

            target = process.ListDocumentsResult(1);
            string targetPiece = target.DO_Piece;
            Log(cfg, "Transformation Sage " + sourceType + " -> " + targetType
                + " : " + sourcePiece + " -> " + targetPiece);
            return targetPiece;
        }
        catch (Exception ex)
        {
            Log(cfg, "Échec transformation depuis " + sourcePiece
                + " (type " + sourceType + ") vers " + targetType + " : " + ex.Message);
            throw;
        }
        finally
        {
            ReleaseCom((object)target);
            ReleaseCom((object)process);
            ReleaseCom((object)source);
            try { if (stream != null) stream.Close(); } catch { }
            ReleaseCom((object)stream);
        }
    }
}
