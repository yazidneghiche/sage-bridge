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
    public List<string> transformationDocuments { get; set; }

    public OrderResult()
    {
        transformationDocuments = new List<string>();
    }
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
                    // Reprendre le test isole valide : initialisation et prix calcules par Sage.
                    // Les prix JSON seront appliques apres la creation du document cible.
                    Log(cfg, "Ajout en cours : " + line.reference
                        + " | quantite=" + line.quantity.ToString(CultureInfo.InvariantCulture));
                    lig.Write();
                    Log(cfg, "Ajout enregistre : " + line.reference);
                }
                finally
                {
                    ReleaseCom((object)lig);
                    ReleaseCom((object)article);
                }
            }
            piece = doc.DO_Piece;
            Log(cfg, "Devis complet enregistré : " + piece
                + " | lignes=" + doc.FactoryDocumentLigne.List.Count);

            if (requestedType == 0)
            {
                ReleaseCom((object)doc);
                doc = null;
                ApplyRequestedPrices(cfg, (object)stream, piece, 0, req.lines);
                return piece;
            }

            // Meme parcours que le test valide : relire dans la connexion encore ouverte.
            ReleaseCom((object)doc);
            doc = null;
            object currentComType = 0;
            object nextComType;
            piece = TransformDocumentInSage(cfg, stream, piece, currentComType, 1,
                documentDate, result, out nextComType);
            currentComType = nextComType;
            result.lastSageDocumentNumber = piece;
            result.lastSageDocumentType = 1;
            ApplyRequestedPrices(cfg, (object)stream, piece, currentComType, req.lines);
            if (requestedType == 1)
                return piece;

            piece = TransformDocumentInSage(cfg, stream, piece, currentComType, 3,
                documentDate, result, out nextComType);
            currentComType = nextComType;
            result.lastSageDocumentNumber = piece;
            result.lastSageDocumentType = 3;

            piece = TransformDocumentInSage(cfg, stream, piece, currentComType, 6,
                documentDate, result, out nextComType);
            result.lastSageDocumentNumber = piece;
            result.lastSageDocumentType = 6;
            return piece;
        }
        finally
        {
            ReleaseCom((object)doc);
            ReleaseCom((object)client);
            try { if (stream != null) stream.Close(); } catch { }
            ReleaseCom((object)stream);
        }

    }

    static string PriceLineKey(string reference, decimal quantity)
    {
        return reference.Trim().ToUpperInvariant() + "|"
            + quantity.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    static void ApplyRequestedPrices(BridgeConfig cfg, object streamObject,
        string piece, object typeCom, List<OrderLine> requestedLines)
    {
        bool hasPrices = false;
        foreach (var requested in requestedLines)
            if (requested.unitPrice.HasValue) hasPrices = true;
        if (!hasPrices) return;

        dynamic stream = streamObject;
        dynamic document = null;
        var heldLines = new List<object>();
        var targets = new List<OrderLine>();
        bool originalAutoPrice = false;
        bool autoPriceChanged = false;
        try
        {
            document = stream.FactoryDocumentVente.ReadPiece(typeCom, piece);
            // Associer par article et quantite, sans supposer l'ordre des lignes Sage.
            var groups = new Dictionary<string, List<OrderLine>>();
            foreach (var requested in requestedLines)
            {
                string key = PriceLineKey(requested.reference, requested.quantity);
                if (!groups.ContainsKey(key)) groups.Add(key, new List<OrderLine>());
                foreach (var other in groups[key])
                    if (other.unitPrice != requested.unitPrice)
                        throw new Exception("Prix ambigus pour un meme article et une meme quantite : "
                            + requested.reference + ". Aucun prix modifie ; controler " + piece);
                groups[key].Add(requested);
            }

            // Verifier toutes les correspondances avant la premiere modification.
            for (int i = 1; i <= document.FactoryDocumentLigne.List.Count; i++)
            {
                dynamic line = null, article = null;
                try
                {
                    line = document.FactoryDocumentLigne.List(i);
                    article = line.Article;
                    if (article == null) continue;
                    string reference = article.AR_Ref;
                    string key = PriceLineKey(reference, Convert.ToDecimal(line.DL_Qte));
                    if (!groups.ContainsKey(key) || groups[key].Count == 0)
                        throw new Exception("Ligne Sage sans correspondance JSON : " + reference
                            + ". Aucun prix modifie ; controler " + piece);
                    OrderLine requested = groups[key][0];
                    groups[key].RemoveAt(0);
                    heldLines.Add((object)line);
                    targets.Add(requested);
                    line = null;
                }
                finally { ReleaseCom((object)article); ReleaseCom((object)line); }
            }
            foreach (var group in groups.Values)
                if (group.Count != 0)
                    throw new Exception("Lignes JSON non retrouvees dans " + piece
                        + ". Aucun prix modifie.");

            originalAutoPrice = stream.FactoryDocumentLigne.AutoSet_PrixLigne;
            stream.FactoryDocumentLigne.AutoSet_PrixLigne = true;
            autoPriceChanged = true;
            for (int i = 0; i < heldLines.Count; i++)
            {
                if (!targets[i].unitPrice.HasValue) continue;
                dynamic line = heldLines[i];
                line.DL_PrixUnitaire = (double)targets[i].unitPrice.Value;
                line.Write();
                Log(cfg, "Prix applique sur " + piece + " | " + targets[i].reference
                    + " | PU HT=" + targets[i].unitPrice.Value.ToString(CultureInfo.InvariantCulture));
            }
            document.RecalculTotaux(true);
            foreach (object line in heldLines) ReleaseCom(line);
            heldLines.Clear();
            ReleaseCom((object)document);
            document = null;
            document = stream.FactoryDocumentVente.ReadPiece(typeCom, piece);

            var expectedPrices = new Dictionary<string, decimal>();
            foreach (var requested in requestedLines)
                if (requested.unitPrice.HasValue)
                    expectedPrices[PriceLineKey(requested.reference, requested.quantity)] = requested.unitPrice.Value;
            int checkedPrices = 0;
            int expectedChecks = 0;
            foreach (var requested in requestedLines)
                if (requested.unitPrice.HasValue) expectedChecks++;
            for (int i = 1; i <= document.FactoryDocumentLigne.List.Count; i++)
            {
                dynamic line = null, article = null;
                try
                {
                    line = document.FactoryDocumentLigne.List(i);
                    article = line.Article;
                    if (article == null) continue;
                    string reference = article.AR_Ref;
                    string key = PriceLineKey(reference, Convert.ToDecimal(line.DL_Qte));
                    if (!expectedPrices.ContainsKey(key)) continue;
                    decimal actual = Convert.ToDecimal(line.DL_PrixUnitaire);
                    if (Math.Abs(actual - expectedPrices[key]) > 0.0001m)
                        throw new Exception("Prix non conserve sur " + piece + " / " + reference
                            + " : attendu " + expectedPrices[key] + ", relu " + actual);
                    checkedPrices++;
                }
                finally { ReleaseCom((object)article); ReleaseCom((object)line); }
            }
            if (checkedPrices != expectedChecks)
                throw new Exception("Nombre de prix verifies inattendu sur " + piece);
            Log(cfg, "Prix verifies apres relecture : " + piece + " | lignes=" + checkedPrices);
        }
        finally
        {
            foreach (object line in heldLines) ReleaseCom(line);
            ReleaseCom((object)document);
            if (autoPriceChanged)
                stream.FactoryDocumentLigne.AutoSet_PrixLigne = originalAutoPrice;
        }
    }

    static string TransformDocumentInSage(
        BridgeConfig cfg, dynamic stream, string sourcePiece, object sourceType,
        int targetType, DateTime documentDate, OrderResult result, out object targetComType)
    {
        targetComType = null;
        dynamic source = null;
        dynamic process = null;
        dynamic target = null;
        try
        {
            source = stream.FactoryDocumentVente.ReadPiece(sourceType, sourcePiece);
            Log(cfg, "Document relu : " + source.DO_Piece
                + " | type COM=" + source.DO_Type
                + " | lignes=" + source.FactoryDocumentLigne.List.Count);

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

            // Transformer le document complet, après enregistrement de toutes ses lignes.
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
            int count = process.ListDocumentsResult.Count;
            var pieces = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                dynamic generated = null;
                try
                {
                    generated = process.ListDocumentsResult(i);
                    string generatedPiece = generated.DO_Piece;
                    pieces.Add(generatedPiece);
                    result.transformationDocuments.Add(targetType + ":" + generatedPiece);
                    Log(cfg, "Résultat transformation " + sourcePiece
                        + " -> type " + targetType + " : " + generatedPiece
                        + " | lignes=" + generated.FactoryDocumentLigne.List.Count);
                    if (count == 1)
                    {
                        target = generated;
                        generated = null;
                    }
                }
                finally { ReleaseCom((object)generated); }
            }

            // Ne jamais continuer vers la facture avec seulement une partie de la vente.
            if (count != 1)
                throw new Exception("Transformation exécutée depuis " + sourcePiece
                    + " : " + count + " document(s) créé(s) ["
                    + string.Join(", ", pieces.ToArray())
                    + "]. Arrêt du parcours ; ne pas réimporter le JSON.");

            targetComType = target.DO_Type;
            string targetPiece = target.DO_Piece;
            Log(cfg, "Type COM retourne : " + targetComType);
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
        }
    }

}
