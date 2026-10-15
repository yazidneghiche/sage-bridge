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

public class OrderRequest
{
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

            // Validation minimale
            if (req == null) throw new Exception("JSON invalide");
            if (string.IsNullOrWhiteSpace(req.customerCode)) throw new Exception("customerCode manquant");
            if (req.lines == null || req.lines.Count == 0) throw new Exception("lines vide");

            // Ouvrir Sage + créer commande
            var docNo = CreateOrderInSage(cfg, req);

            result.success = true;
            result.sageDocumentNumber = docNo;
            result.message = "Commande créée";
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = "Erreur création commande";
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

	static string CreateOrderInSage(BridgeConfig cfg, OrderRequest req)
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

            // CLIENT
            dynamic client = stream.CptaApplication.FactoryClient.ReadNumero(req.customerCode);

            // DEVIS
            dynamic doc = stream.FactoryDocumentVente.CreateType(0);

            doc.SetDefaultClient(client);
            doc.DO_Date = DateTime.Now;
            doc.DO_Statut = 2; // status 2 . for converting to BC
            doc.DO_RefExterne = req.order_reference;
            doc.DO_Ref = req.reference;

            // On ajoute le nom du contact
            if (!string.IsNullOrEmpty(req.contact))
            {
                doc.DO_Contact = req.contact;
            }
           // doc.DO_Contact = (req.firstname + " " + req.lastname).Trim();

            Log(cfg, "Création devis pour client : " + req.customerCode);
            //Log(cfg, "Création devis pour client : " + req.customerCode);
            doc.Write();

            foreach (var l in req.lines)
            {
                dynamic article = stream.FactoryArticle.ReadReference(l.reference);
                dynamic lig = doc.FactoryDocumentLigne.Create();

                lig.SetDefaultArticle(article, (double)l.quantity);

                if (l.unitPrice.HasValue)
                    lig.DL_PrixUnitaire = (double)l.unitPrice.Value;

                lig.Write();
            }

            string devisPiece = doc.DO_Piece;

            Log(cfg, "Devis créé : " + devisPiece);

            stream.Close();

            // Trnsformation du Devis en commande
            string bcPiece = TransformQuoteToOrderInSage(cfg, devisPiece);
            Log(cfg, "BC créé : " + bcPiece);

            return bcPiece;
        }
        finally
        {
            if (stream != null)
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
        }
    }

	static string TransformQuoteToOrderInSage(BridgeConfig cfg, string devisPiece)
    {
        dynamic stream = null;
        dynamic pTransfo = null;
        dynamic devis = null;

        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd  = cfg.SagePassword;
            stream.Open();

            // 0 = devis vente dans ta base
            devis = stream.FactoryDocumentVente.ReadPiece(0, devisPiece);

            // Processus Commander = devis -> bon de commande
            pTransfo = stream.Transformation.Vente.CreateProcess_Commander();

            // Ajout du devis au process
            pTransfo.AddDocument(devis);

            // Optionnel : date du document généré
            try { pTransfo.DO_Date = DateTime.Now; } catch { }

            // Vérification
            if (!pTransfo.CanProcess)
            {
                string msg = "Transformation impossible";
                try
                {
                    if (pTransfo.Errors.Count > 0)
                    {
                        msg = "";
                        for (int i = 1; i <= pTransfo.Errors.Count; i++)
                        {
                            var err = pTransfo.Errors.Item(i);
                            msg += "[" + err.ErrorCode + "] " + err.Text + " ";
                        }
                    }
                }
                catch { }
                throw new Exception(msg);
            }

            // Transformation
            pTransfo.Process();

            // 1er document résultat = le BC créé
            string bcPiece = pTransfo.ListDocumentsResult(1).DO_Piece;

            stream.Close();
            return bcPiece;
        }
        finally
        {
            try { if (devis != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(devis); } catch { }
            try { if (pTransfo != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(pTransfo); } catch { }
            try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
        }
    }

}