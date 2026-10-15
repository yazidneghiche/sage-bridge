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
     public BridgeConfig()
        {
            PollMs = 1000;
        }
}

public class OrderRequest
{
    public string customerCode { get; set; }
    public string date { get; set; } // yyyy-MM-dd
    public string reference { get; set; }
    public string warehouse { get; set; }
    public List<OrderLine> lines { get; set; }
}

public class OrderLine
{
    public string itemCode { get; set; }
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



static void DumpComMembers(BridgeConfig cfg, object comObj, string label)
{
    try
    {
        var t = comObj.GetType();
        var dumpPath = Path.Combine(Path.GetDirectoryName(cfg.LogFile) ?? cfg.ResultsDir, "com_dump.txt");

        var sb = new StringBuilder();
        sb.AppendLine("==== " + label + " ====");
        sb.AppendLine("Type: " + t.FullName);

        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        Array.Sort(members, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var m in members)
        {
            sb.AppendLine(m.MemberType.ToString().PadRight(10) + " " + m.Name);
        }

        sb.AppendLine();
        File.AppendAllText(dumpPath, sb.ToString(), Encoding.UTF8);

        Log(cfg, "COM dump écrit: " + dumpPath + " (" + label + ")");
    }
    catch (Exception ex)
    {
        Log(cfg, "COM dump erreur (" + label + "): " + ex.Message);
    }
}
  static string CreateOrderInSageOld(BridgeConfig cfg, OrderRequest req)
  {
      dynamic stream = null;
      dynamic doc = null;

      try
      {
          var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
          stream = Activator.CreateInstance(t);

          // Connexion
          stream.Name = cfg.GcmPath;
          stream.Loggable.UserName = cfg.SageUser;
          stream.Loggable.UserPwd = cfg.SagePassword;
          stream.Open();

          // 1 = Bon de commande (vente)
          const int DocType_BonCommande = 1;

          // Création document
          doc = stream.FactoryDocumentVente.CreateType(DocType_BonCommande);

          // Client (CT_Num)
          dynamic client = stream.CptaApplication.FactoryClient.ReadNumero(req.customerCode);
          doc.SetDefaultClient(client);

          // En-tête par défaut
          doc.WriteDefault();

          // Lignes
          foreach (var l in req.lines)
          {
              dynamic article = stream.FactoryArticle.ReadReference(l.itemCode);

              dynamic docLine = doc.FactoryDocumentLigne.Create();
              docLine.SetDefaultArticle(article, (double)l.quantity);
              docLine.WriteDefault();

              // (Optionnel) prix unitaire si tu veux tenter
              // Attention: selon paramétrage Sage, le prix peut être recalculé
              if (l.unitPrice.HasValue)
              {
                  try { docLine.DL_PrixUnitaire = (double)l.unitPrice.Value; } catch { }
              }
          }

          // Enregistrement + recalcul
          // (Suivant version Sage, RecalculTotaux(true) est très pratique)
          try
          {
              doc.RecalculTotaux(true);
          }
          catch
          {
              // fallback si la méthode n’existe pas
              try { doc.Save(); } catch { doc.Write(); }
          }

          // Récupérer le numéro de pièce (DO_Piece)
          string doPiece = null;
          try { doPiece = (string)doc.DO_Piece; } catch { }
          if (string.IsNullOrWhiteSpace(doPiece))
          {
              try { doPiece = (string)doc.Piece; } catch { }
          }
          if (string.IsNullOrWhiteSpace(doPiece))
          {
              doPiece = "OK_SANS_NUM"; // fallback (rare)
          }

          stream.Close();
          return doPiece;
      }
      finally
      {
          try { if (doc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(doc); } catch { }
          try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
      }
  }
    static string CreateOrderInSageV1(BridgeConfig cfg, OrderRequest req)
    {
        dynamic stream = null;
        // Test lecture client (CT_Num)
        dynamic client = stream.CptaApplication.FactoryClient.ReadNumero(req.customerCode);
        var ctNum = "";
        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();
            DumpComMembers(cfg, stream, "Objets100c.Cial.Stream");

            // ======== TODO SAGE COM : création commande client ========
            // Ici on mettra le vrai code (doc type, souche, dépôt, etc.)
            // Pour l’instant, on confirme juste que l’ouverture marche :
            // ==========================================================

            // Exemple placeholder : retourne un numéro fictif
            // -> On remplacera par doc.Numero quand la création est codée.
            var fakeDocNo = "CMD_TEST_" + DateTime.Now.ToString("HHmmss");

            try { ctNum = (string)client.CT_Num; } catch { }
            		if (string.IsNullOrWhiteSpace(ctNum))
            		{
            			// fallback si propriété pas accessible
            			ctNum = req.customerCode;
            		}
            stream.Close();
            return fakeDocNo;
        }
        finally
        {
            try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
        }
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
        doc.DO_Statut = 2;
		Log(cfg, "Création devis pour client : " + req.customerCode);
        doc.Write();

        foreach (var l in req.lines)
        {
            dynamic article = stream.FactoryArticle.ReadReference(l.itemCode);
            dynamic lig = doc.FactoryDocumentLigne.Create();

            lig.SetDefaultArticle(article, (double)l.quantity);

            if (l.unitPrice.HasValue)
                lig.DL_PrixUnitaire = (double)l.unitPrice.Value;

            lig.Write();
        }

        string devisPiece = doc.DO_Piece;

        Log(cfg, "Devis créé : " + devisPiece);

        stream.Close();

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
static string CreateOrderInSageV90(BridgeConfig cfg, OrderRequest req)
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



        // DOCUMENT
     // 2) création ENTÊTE au bon type (DO_Type = 1 => Commande)
		dynamic doc = stream.FactoryDocumentVente.CreateType(0);

		doc.SetDefaultClient(client);
		//doc.SetDefaultDevise();
		//doc.SetDefaultExpedition();
		//doc.SetDefaultCondition();
		//doc.SetDefaultTarif();

		//doc.SetDefaultDO_Piece();
		doc.DO_Date = DateTime.Now;
		doc.DO_Statut = 2;
		//doc.DO_Expedit   = 1;
		//doc.DO_Condition = 1;
		//doc.DO_Tarif     = 1;
		//doc.DO_Langue    = 11;
		//doc.DO_Regime    = 21;

		// (optionnel) si tu veux une ref
		//if (!string.IsNullOrWhiteSpace(req.reference))
			//doc.DO_Ref = req.reference;

		// Entête
		doc.Write();

		// 4) lignes
		foreach (var l in req.lines)
		{
			dynamic article = stream.FactoryArticle.ReadReference(l.itemCode);

			// la factory correcte d’après la doc Sage
			dynamic lig = doc.FactoryDocumentLigne.Create();

			lig.SetDefaultArticle(article, (double)l.quantity);

			if (l.unitPrice.HasValue)
				lig.DL_PrixUnitaire = (double)l.unitPrice.Value;

			lig.Write();
		}



        string piece = doc.DO_Piece;

        stream.Close();
		string bcPiece = TransformQuoteToOrderInSage(cfg, piece);
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
    static string CreateOrderInSagev4(BridgeConfig cfg, OrderRequest req)
    {
        dynamic stream = null;
        dynamic doc = null;

        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd  = cfg.SagePassword;
            stream.Open();



			// Lire un BC existant pour récupérer le vrai DO_Type au format attendu par COM
			dynamic existing = null;
			try
			{
				// Selon versions : ReadPiece / ReadNumero / ReadReference
				object doType = Convert.ToInt16(1);      // important: Int16
			    existing = stream.FactoryDocumentVente.ReadPiece(doType, "BC157550");
			}
			catch
			{
				try { existing = stream.FactoryDocumentVente.ReadNumero("BC157550"); } catch { }
			}

			if (existing == null)
				throw new Exception("Impossible de lire le document BC157550 via FactoryDocumentVente (ReadPiece/ReadNumero).");

			var existingType = existing.DO_Type; // IMPORTANT : ne pas caster !
			Log(cfg, "Type BC157550 (DO_Type) = " + existingType);

			// Créer le même type
			doc = stream.FactoryDocumentVente.CreateType(existingType);
			Log(cfg, "CreateType(existing.DO_Type) OK");

            // 1) Client (CHEMIN VALIDÉ CHEZ TOI)
            dynamic client = stream.CptaApplication.FactoryClient.ReadNumero(req.customerCode);
            if (client == null) throw new Exception("Client introuvable: " + req.customerCode);

            // 2) Créer document vente (commande)
            // NOTE: docType peut varier selon installation. On commence avec 1.
           // int docType = 1;
           // doc = stream.FactoryDocumentVente.CreateType(docType);
		  // int[] candidates = new int[] { 1, 2, 3, 4, 5, 6, 7 };
          try
			{
				//short doType = 1;
				//doc = stream.FactoryDocumentVente.CreateType(doType);
			}
			catch
			{
				// fallback : certaines versions refusent CreateType en late-binding
				//doc = stream.FactoryDocumentVente.Create();
				//doc.DO_Type = (short)1;
			}

            if (doc == null)
            {
               // throw new Exception("Impossible de créer un document vente. Dernière erreur: " + (last != null ? last.Message : "N/A"));
            }
            // Affecte le client
            // Selon versions: SetDefaultClient existe. Sinon DO_Tiers.
            try
            {
                doc.SetDefaultClient(client);
            }
            catch
            {
                doc.DO_Tiers = req.customerCode;
            }

            // Init doc
            try { doc.WriteDefault(); } catch { }

            // 3) Lignes
            foreach (var l in req.lines)
            {
                // Article
                dynamic art = stream.FactoryArticle.ReadReference(l.itemCode);
                if (art == null) throw new Exception("Article introuvable: " + l.itemCode);

                // Ligne
                dynamic line = doc.FactoryDocumentLigne.Create();

                // Méthode la plus simple si dispo
                bool setOk = false;
                try { line.SetDefaultArticle(art, (double)l.quantity); setOk = true; } catch { }
                if (!setOk)
                {
                    // fallback champs
                    try { line.AR_Ref = l.itemCode; } catch { }
                    try { line.DL_Qte = (double)l.quantity; } catch { }
                }

                // Prix unitaire optionnel
                if (l.unitPrice.HasValue)
                {
                    try { line.DL_PrixUnitaire = (double)l.unitPrice.Value; } catch { }
                }

                // Sauver la ligne
                try { line.WriteDefault(); } catch { line.Write(); }
            }

            // 4) Sauver doc
            try { doc.RecalculTotaux(true); } catch { }
            try { doc.WriteDefault(); } catch { doc.Write(); }

            // Numéro pièce
            string doPiece = null;
            try { doPiece = (string)doc.DO_Piece; } catch { }
            if (string.IsNullOrWhiteSpace(doPiece)) doPiece = "OK_SANS_NUM";

            stream.Close();
            return doPiece;
        }
        finally
        {
            try { if (doc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(doc); } catch { }
            try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
        }
    }
    static string CreateOrderInSageVfinal(BridgeConfig cfg, OrderRequest req)
    {
        dynamic stream = null;
        dynamic doc = null;

        try
        {
            // 1) Stream + Open
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            stream.Name = cfg.GcmPath;

            // Loggable peut être null sur certaines versions -> on test
            dynamic loggable = TryGetProp(stream, "Loggable");
            if (loggable == null) throw new Exception("stream.Loggable est null (COM Sage).");
            loggable.UserName = cfg.SageUser;
            loggable.UserPwd  = cfg.SagePassword;

            stream.Open();

            // 2) Récupérer le client (CT_Num)
            dynamic client = ReadClient(stream, req.customerCode);
            if (client == null) throw new Exception("Client introuvable: " + req.customerCode);

            // 3) Créer commande (DocumentTypeVenteCommande ≈ 1 sur beaucoup d'install)
            // Si chez toi ce n’est pas 1, on ajustera (ou on le mettra en config).
            doc = CreateSalesOrderDocument(stream, 1);

            // 4) Affecter le client au document
            if (!TryCall(doc, "SetDefaultClient", client))
            {
                // fallback: certains modèles utilisent DO_Tiers / DO_TiersPayeur / etc.
                if (!TrySetProp(doc, "DO_Tiers", req.customerCode))
                    if (!TrySetProp(doc, "DO_Tiers", GetStringProp(client, "CT_Num")))
                        throw new Exception("Impossible d'affecter le client au document (SetDefaultClient/DO_Tiers).");
            }

            // Date / ref (optionnel)
            if (!string.IsNullOrWhiteSpace(req.date))
            {
                DateTime d;
                if (DateTime.TryParse(req.date, out d))
                    TrySetProp(doc, "DO_Date", d);
            }
            if (!string.IsNullOrWhiteSpace(req.reference))
                TrySetProp(doc, "DO_Ref", req.reference);

            // Init document (selon version)
            TryCall(doc, "SetDefault");
            TryCall(doc, "WriteDefault");

            // 5) Ajouter lignes
            for (int i = 0; i < req.lines.Count; i++)
            {
                var l = req.lines[i];

                dynamic article = ReadArticle(stream, l.itemCode);
                if (article == null) throw new Exception("Article introuvable: " + l.itemCode);

                dynamic line = CreateDocLine(doc);
                if (line == null) throw new Exception("Impossible de créer une ligne (FactoryDocumentLigne/FactoryDocumentVenteLigne).");

                // Méthode 1 (courante): SetDefaultArticle(article, qte)
                bool ok = false;
                ok = TryCall(line, "SetDefaultArticle", article, (double)l.quantity);
                if (!ok) ok = TryCall(line, "SetDefaultArticle", article);

                // Méthode 2: affectation champs
                if (!ok)
                {
                    TrySetProp(line, "AR_Ref", l.itemCode);
                    TrySetProp(line, "DL_Qte", (double)l.quantity);
                }

                // Prix unitaire optionnel
                if (l.unitPrice.HasValue)
                {
                    TrySetProp(line, "DL_PrixUnitaire", (double)l.unitPrice.Value);
                }

                // Finaliser ligne
                TryCall(line, "SetDefault");
                if (!TryCall(line, "WriteDefault"))
                    TryCall(line, "Write");
            }

            // 6) Enregistrer document
            if (!TryCall(doc, "RecalculTotaux", true))
                TryCall(doc, "RecalculTotaux");

            if (!TryCall(doc, "WriteDefault"))
                if (!TryCall(doc, "Write"))
                    if (!TryCall(doc, "Save"))
                        throw new Exception("Impossible d'enregistrer le document (WriteDefault/Write/Save).");

            // 7) Récupérer numéro
            string doPiece = GetStringProp(doc, "DO_Piece");
            if (string.IsNullOrWhiteSpace(doPiece))
                doPiece = GetStringProp(doc, "Piece");

            if (string.IsNullOrWhiteSpace(doPiece))
                doPiece = "OK_SANS_NUM";

            // Close
            TryCall(stream, "Close");
            return doPiece;
        }
        finally
        {
            try { if (doc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(doc); } catch { }
            try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
        }
    }

    // -------- Helpers --------

    static dynamic ReadClient(dynamic stream, string ctNum)
    {
        // essaye stream.FactoryClient.ReadNumero(ctNum)
        dynamic fc = TryGetProp(stream, "FactoryClient");
        if (fc != null)
        {
            try { return fc.ReadNumero(ctNum); } catch { }
            try { return fc.ReadReference(ctNum); } catch { }
        }

        // essaye stream.CptaApplication.FactoryClient.ReadNumero(ctNum)
        dynamic cpta = TryGetProp(stream, "CptaApplication");
        if (cpta != null)
        {
            dynamic fc2 = TryGetProp(cpta, "FactoryClient");
            if (fc2 != null)
            {
                try { return fc2.ReadNumero(ctNum); } catch { }
            }
        }

        // essaye stream.ComptaApplication.FactoryClient.ReadNumero(ctNum)
        dynamic compta = TryGetProp(stream, "ComptaApplication");
        if (compta != null)
        {
            dynamic fc3 = TryGetProp(compta, "FactoryClient");
            if (fc3 != null)
            {
                try { return fc3.ReadNumero(ctNum); } catch { }
            }
        }

        return null;
    }

    static dynamic ReadArticle(dynamic stream, string arRef)
    {
        dynamic fa = TryGetProp(stream, "FactoryArticle");
        if (fa != null)
        {
            try { return fa.ReadReference(arRef); } catch { }
            try { return fa.ReadNumero(arRef); } catch { }
        }
        return null;
    }

    static dynamic CreateSalesOrderDocument(dynamic stream, int docType)
    {
        // FactoryDocumentVente.CreateType(docType)
        dynamic fdv = TryGetProp(stream, "FactoryDocumentVente");
        if (fdv != null)
        {
            try { return fdv.CreateType(docType); } catch { }
            try { return fdv.Create(); } catch { }
        }

        // fallback: FactoryDocument.CreateType
        dynamic fd = TryGetProp(stream, "FactoryDocument");
        if (fd != null)
        {
            try { return fd.CreateType(docType); } catch { }
            try { return fd.Create(); } catch { }
        }

        throw new Exception("FactoryDocumentVente/FactoryDocument introuvable sur stream.");
    }

    static dynamic CreateDocLine(dynamic doc)
    {
        // doc.FactoryDocumentLigne.Create()
        dynamic f1 = TryGetProp(doc, "FactoryDocumentLigne");
        if (f1 != null)
        {
            try { return f1.Create(); } catch { }
        }

        // doc.FactoryDocumentVenteLigne.Create()
        dynamic f2 = TryGetProp(doc, "FactoryDocumentVenteLigne");
        if (f2 != null)
        {
            try { return f2.Create(); } catch { }
        }

        return null;
    }

    static dynamic TryGetProp(dynamic obj, string prop)
    {
        try { return obj.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, obj, null); }
        catch { return null; }
    }

    static bool TrySetProp(dynamic obj, string prop, object val)
    {
        try
        {
            obj.GetType().InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, obj, new object[] { val });
            return true;
        }
        catch { return false; }
    }

    static bool TryCall(dynamic obj, string method, params object[] args)
    {
        try
        {
            obj.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, obj, args);
            return true;
        }
        catch { return false; }
    }

    static string GetStringProp(dynamic obj, string prop)
    {
        try
        {
            var v = obj.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, obj, null);
            return v != null ? v.ToString() : null;
        }
        catch { return null; }
    }
}