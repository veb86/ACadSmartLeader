using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(ACadSmartLeader.Plugin))]
namespace ACadSmartLeader;

public sealed class Plugin : IExtensionApplication
{
    private static readonly Dictionary<Document, DocumentState> documents = new Dictionary<Document, DocumentState>();
    internal static UserSettings Settings { get; } = UserSettings.Load();
    private readonly StartupReadiness readiness = new StartupReadiness();
    public void Initialize()
    {
        EmbeddedAssemblies.Register();
        foreach (Document document in AcApplication.DocumentManager) For(document);
        AcApplication.DocumentManager.DocumentCreated += Created;
        AcApplication.DocumentManager.DocumentCreateStarted += Opening;
        AcApplication.DocumentManager.DocumentCreationCanceled += OpenCancelled;
        AcApplication.DocumentManager.DocumentToBeDestroyed += Destroyed;
        AcApplication.Idle += Idle;
    }
    internal static DocumentState For(Document document)
    {
        if (!documents.TryGetValue(document, out var state)) documents.Add(document, state = new DocumentState(document));
        return state;
    }
    internal static void ClearPending() { foreach (var state in documents.Values) state.Schedule.Clear(); }
    private void Opening(object sender, DocumentCollectionEventArgs e) => readiness.DocumentCreateStarted();
    private void OpenCancelled(object sender, DocumentCollectionEventArgs e) => readiness.DocumentCreateFinished();
    private void Created(object sender, DocumentCollectionEventArgs e) { readiness.DocumentCreateFinished(); For(e.Document); }
    private void Destroyed(object sender, DocumentCollectionEventArgs e)
    {
        if (documents.TryGetValue(e.Document, out var state)) { documents.Remove(e.Document); state.Dispose(); }
    }
    private void Idle(object? sender, EventArgs e)
    {
        var document = AcApplication.DocumentManager.MdiActiveDocument;
        if (document == null || !readiness.IsReady(true, AcApplication.IsQuiescent, document.Editor.IsQuiescent, document.CommandInProgress)) return;
        var state = For(document);
        if (state.Schedule.Dispatch(true))
        {
            state.Trace("Поставлена команда SL_UPDATE после регенерации.");
            document.SendStringToExecute("_SL_UPDATE _All ", true, false, false);
        }
    }
    public void Terminate()
    {
        AcApplication.Idle -= Idle;
        AcApplication.DocumentManager.DocumentCreated -= Created;
        AcApplication.DocumentManager.DocumentCreateStarted -= Opening;
        AcApplication.DocumentManager.DocumentCreationCanceled -= OpenCancelled;
        AcApplication.DocumentManager.DocumentToBeDestroyed -= Destroyed;
        foreach (var state in documents.Values.ToArray()) state.Dispose(); documents.Clear();
    }
}
