using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalWorkspace.Core;
using PersonalWorkspace.Data;
using PersonalWorkspace.Data.Migrations;
using PersonalWorkspace.Services;
using Xunit;

namespace PersonalWorkspace.Tests;

public sealed partial class TrackerTests
{
    private AssetFileStore AssetFiles() => new(new(paths));
    private AssetService Assets(IAssetFileStore? store = null) => new(new SqliteContentRepository(), store ?? AssetFiles(), paths, current, gate, clock, NullLogger<AssetService>.Instance);
    private RichContentService Rich() => new(new SqliteContentRepository(), current, gate, clock, NullLogger<RichContentService>.Instance);
    private ContentKey Key(Guid id) => new(profile, id);
    private async Task<string> SourceFile(string name = "notes.txt", byte[]? bytes = null)
    {
        var directory = Path.Combine(root, "imports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name); await File.WriteAllBytesAsync(file, bytes ?? Encoding.UTF8.GetBytes("Local attachment\n")); return file;
    }
    private string AssetPath(Asset asset) => new AssetPaths(paths).Resolve(profile, asset.StorageRelativePath);
    private string[] StoredFiles() => Directory.GetFiles(paths.ProfileAttachments(profile), "*", SearchOption.AllDirectories);
    private static readonly byte[] PngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task RichContentMigrationUpgradesPopulatedPhaseSixteenWithoutChangingExistingData()
    {
        var legacy=Guid.NewGuid();new ProfileFiles(paths).Create(legacy);
        await Sql("CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY,Name TEXT NOT NULL,AppliedAtUtc TEXT NOT NULL);",legacy);
        foreach(var m in WorkspaceMigrationCatalog.All.Take(14)) await Sql(m.Sql+$"INSERT INTO SchemaMigrations VALUES({m.Version},'{m.Name}','original');",legacy);
        var page=Guid.NewGuid();var list=Guid.NewGuid();var block=Guid.NewGuid();var widget=Guid.NewGuid();var stamp="2026-10-12T00:00:00+00:00";
        await Sql($"""
            INSERT INTO WorkspaceItems VALUES('{page}',5,'Existing Page','{stamp}','{stamp}',NULL,NULL);
            INSERT INTO WorkspaceItems VALUES('{list}',6,'Existing List','{stamp}','{stamp}',NULL,NULL);
            INSERT INTO Pages VALUES('{page}',NULL,0,NULL);
            INSERT INTO Lists VALUES('{list}','Keep description',1,1,'EUR');
            INSERT INTO ListItems VALUES('{Guid.NewGuid()}','{list}','Milk',1,NULL,'1.25',NULL,NULL,0,'{stamp}','{stamp}');
            INSERT INTO PageCanvasItems VALUES('{block}','{page}',NULL,0,24,48,400,320,'','{stamp}','{stamp}');
            INSERT INTO WidgetInstances VALUES('{widget}','{block}',6,'{list}',8,'{stamp}','{stamp}');
            UPDATE WidgetListSettings SET ItemFilter=1 WHERE WidgetId='{widget}';
            """,legacy);
        string[] tables=["WorkspaceItems","Pages","Lists","ListItems","PageCanvasItems","WidgetInstances","WidgetListSettings"];
        var snapshots=new Dictionary<string,string>();foreach(var t in tables)snapshots[t]=await TableRows(legacy,t);
        await initializer.InitializeAsync(legacy,false);await initializer.InitializeAsync(legacy,false);
        foreach(var t in tables)Assert.Equal(snapshots[t],await TableRows(legacy,t));
        Assert.Equal(15L,await Sql("SELECT COUNT(*) FROM SchemaMigrations;",legacy));Assert.Equal(14L,await Sql("SELECT COUNT(*) FROM SchemaMigrations WHERE AppliedAtUtc='original';",legacy));
        Assert.Null(await Sql("PRAGMA foreign_key_check;",legacy)); Assert.Empty(Directory.GetFiles(paths.ProfileAttachments(legacy),"*",SearchOption.AllDirectories));
        await using var global=await new SqliteConnectionFactory(paths).OpenAsync();using var command=global.CreateCommand();command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Assets','RichTextDocuments')";Assert.Equal(0L,await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task AssetImportPersistsGuidMetadataHashAndIndependentSameNameImports()
    {
        var owner=await List();var first=await SourceFile();var second=await SourceFile(bytes:[1,2,3]);var a=await Assets().ImportAsync(LRef(owner),first);var b=await Assets().ImportAsync(LRef(owner),second);
        Assert.NotEqual(a.Asset.Id,b.Asset.Id);Assert.NotEqual(a.Asset.StorageRelativePath,b.Asset.StorageRelativePath);Assert.Equal("notes.txt",a.Asset.OriginalFileName);
        Assert.False(Path.IsPathRooted(a.Asset.StorageRelativePath));Assert.DoesNotContain("notes",a.Asset.StorageRelativePath);Assert.Equal(new FileInfo(first).Length,a.Asset.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first))),a.Asset.ContentHash);Assert.Equal("text/plain",a.Asset.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(first),await File.ReadAllBytesAsync(AssetPath(a.Asset)));Assert.Equal(a.Asset,await Assets().GetAsync(Key(a.Asset.Id)));
        Assert.Equal(2,(await Assets().ListAsync(LRef(owner))).Count);Assert.Empty(await Assets().CheckIntegrityAsync(profile));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));
        var same=await Assets().ImportAsync(LRef(owner),first);Assert.NotEqual(a.Asset.Id,same.Asset.Id);Assert.Equal(a.Asset.ContentHash,same.Asset.ContentHash);
        var reserved=await Assets().ImportAsync(LRef(owner),await SourceFile("notes.import"));Assert.Equal("notes.import",reserved.Asset.OriginalFileName);Assert.EndsWith(".bin",reserved.Asset.StorageRelativePath);Assert.True(File.Exists(AssetPath(reserved.Asset)));Assert.Empty(await Assets().CheckIntegrityAsync(profile));
    }
    [Theory]
    [InlineData("../outside.txt")][InlineData("C:/outside.txt")][InlineData("/outside")][InlineData("aa/../../outside")][InlineData("aa\\file.txt")][InlineData("aa/file.txt:stream")][InlineData("aa/file.txt")]
    public void AssetPathsRejectTraversalAbsoluteAndUnmanagedNames(string relative) => Assert.Throws<ContentValidationException>(()=>new AssetPaths(paths).Resolve(profile,relative));
    [Fact]
    public async Task AssetStorageNeverUsesDisplayNameAndNeverOverwrites()
    {
        var id=Guid.NewGuid();var relative=AssetPaths.Relative(id,"../../malicious.exe");Assert.DoesNotContain("malicious",relative);Assert.DoesNotContain("..",relative);var job=new AssetImportJob(id,relative);
        var target=new AssetPaths(paths).Resolve(profile,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);await File.WriteAllTextAsync(target,"keep");
        Assert.Throws<ContentValidationException>(()=>AssetFiles().CheckImportDestination(profile,job));Assert.Equal("keep",await File.ReadAllTextAsync(target));
        Assert.Throws<ContentValidationException>(()=>AssetPaths.ValidateIdentity(Guid.NewGuid(),relative));
    }
    [Fact]
    public async Task AssetSharedReferencesAndLastRemovalControlPhysicalCleanup()
    {
        var a=await List();var b=await List("Other");var asset=await Assets().ImportAsync(LRef(a),await SourceFile());var second=await Assets().AddReferenceAsync(LRef(b),Key(asset.Asset.Id));
        var removed=await Assets().RemoveReferenceAsync(Key(asset.Reference.Id));Assert.Empty(removed.Pending);Assert.True(File.Exists(AssetPath(asset.Asset)));Assert.Single(await Assets().ListAsync(LRef(b)));
        removed=await Assets().RemoveReferenceAsync(Key(second.Id));Assert.Equal(1,removed.Removed);Assert.Empty(removed.Pending);Assert.False(File.Exists(AssetPath(asset.Asset)));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Fact]
    public async Task AssetOwnerLifecyclePreservesSharedFilesAndPermanentDeletionQueuesOnlyOrphans()
    {
        var a=await List();var b=await List("Other");var asset=await Assets().ImportAsync(LRef(a),await SourceFile());await Assets().AddReferenceAsync(LRef(b),Key(asset.Asset.Id));
        foreach(var action in new[]{ListAction.Archive,ListAction.RestoreArchive,ListAction.Trash,ListAction.RestoreTrash})
        { await Lists().ApplyAsync(LRef(a),action);Assert.Equal(asset,Assert.Single(await Assets().ListAsync(LRef(a))));Assert.True(File.Exists(AssetPath(asset.Asset))); }
        await Lists().ApplyAsync(LRef(a),ListAction.Trash);await Lists().DeleteAsync(LRef(a));await Assets().CleanupAsync(profile);Assert.True(File.Exists(AssetPath(asset.Asset)));
        await Lists().ApplyAsync(LRef(b),ListAction.Trash);await Lists().DeleteAsync(LRef(b));Assert.Equal(1L,await Sql("SELECT PendingDelete FROM Assets;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetReferences;"));
        Assert.Single(await Assets().CheckIntegrityAsync(profile));await Assets().CleanupAsync(profile);Assert.Empty(StoredFiles());Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task AssetDeleteFailureIsDurableAndRetryableAndCannotBeReferencedAgain()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());var faulty=new FaultyAssetStore(AssetFiles()){FailDelete=true};
        var report=await Assets(faulty).RemoveReferenceAsync(Key(asset.Reference.Id));Assert.Equal(asset.Asset.Id,Assert.Single(report.Pending));Assert.True(File.Exists(AssetPath(asset.Asset)));
        Assert.Equal(1L,await Sql("SELECT PendingDelete FROM Assets;"));await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().AddReferenceAsync(LRef(owner),Key(asset.Asset.Id)));
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"INSERT INTO AssetReferences VALUES('{Guid.NewGuid()}','{asset.Asset.Id}','{owner.Item.Id}',0,0,'now');"));
        Assert.Empty((await Assets().CleanupAsync(profile)).Pending);Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Theory]
    [InlineData("Assets")][InlineData("AssetReferences")]
    public async Task AssetMetadataFailureCompensatesPublishedFilesAndLeavesNoBrokenRows(string table)
    {
        var owner=await List();await Sql($"CREATE TRIGGER FailContent BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'test'); END;");
        await Assert.ThrowsAsync<ContentOperationException>(()=>Assets().ImportAsync(LRef(owner),SourceFile().GetAwaiter().GetResult()));
        Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetReferences;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));
    }
    [Fact]
    public async Task AssetFailedImportAndCleanupLeaveRecoveryRecordUntilRetry()
    {
        var owner=await List();var faulty=new FaultyAssetStore(AssetFiles()){FailAfterImport=true,FailDelete=true};
        await Assert.ThrowsAsync<ContentOperationException>(()=>Assets(faulty).ImportAsync(LRef(owner),SourceFile().GetAwaiter().GetResult()));
        Assert.Single(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));Assert.Equal(1L,await Sql("SELECT COUNT(*) FROM AssetImports;"));
        await Assets().CleanupAsync(profile);Assert.Empty(StoredFiles());Assert.Empty(await Assets().CheckIntegrityAsync(profile));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AssetInterruptedImportRecoveryCoversPartialAndRenamedFiles(bool published)
    {
        var owner=await List();var id=Guid.NewGuid();var job=new AssetImportJob(id,AssetPaths.Relative(id,"notes.txt"));
        await new SqliteContentRepository().BeginImportAsync(new(profile,paths.WorkspaceDatabase(profile)),owner.Item.Id,job,default);
        var file=new AssetPaths(paths).Resolve(profile,published?job.StorageRelativePath:job.StagingRelativePath);Directory.CreateDirectory(Path.GetDirectoryName(file)!);await File.WriteAllTextAsync(file,"partial");
        var result=await Assets().CleanupAsync(profile);Assert.Equal(1,result.Removed);Assert.Empty(result.Pending);Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));
    }
    [Fact]
    public async Task AssetMissingSourceAndCancellationCreateNoBrokenReference()
    {
        var owner=await List();await Assert.ThrowsAsync<ContentOperationException>(()=>Assets().ImportAsync(LRef(owner),Path.Combine(root,"missing.txt")));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Assets().ImportAsync(LRef(owner),"unused.txt",cancelled.Token));
        Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Fact]
    public async Task AssetImportAndReadStreamLargeFilesInBoundedBuffers()
    {
        var source=await SourceFile();await using(var output=File.Create(source)){var chunk=new byte[81920];Random.Shared.NextBytes(chunk);for(var i=0;i<160;i++)await output.WriteAsync(chunk);}
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),source);using var destination=new CountingStream();await Assets().CopyToAsync(Key(asset.Asset.Id),destination);
        Assert.Equal(81920L*160,asset.Asset.ByteLength);Assert.Equal(asset.Asset.ByteLength,destination.Count);Assert.True(destination.MaxWrite<=131072);Assert.Empty(await Assets().CheckIntegrityAsync(profile));
    }
    [Theory]
    [InlineData("photo.png")][InlineData("photo.bin")]
    public async Task AssetPngRecognitionUsesContentAndStoresDimensions(string name)
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile(name,PngBytes));
        Assert.Equal("image/png",asset.Asset.MediaType);Assert.Equal(1,asset.Asset.PixelWidth);Assert.Equal(1,asset.Asset.PixelHeight);Assert.Equal(name,asset.Asset.OriginalFileName);Assert.Equal(PngBytes,await File.ReadAllBytesAsync(AssetPath(asset.Asset)));
    }
    [Theory]
    [InlineData("photo.png")][InlineData("photo.jpg")][InlineData("photo.jpeg")]
    public async Task AssetInvalidImageExtensionIsRejectedWithoutGarbage(string name)
    {
        var owner=await List();await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().ImportAsync(LRef(owner),SourceFile(name,[1,2,3,4]).GetAwaiter().GetResult()));Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Fact]
    public async Task AssetGenericPdfAndUnknownFilesNeedNoDocumentParser()
    {
        var owner=await List();var pdf=await Assets().ImportAsync(LRef(owner),await SourceFile("document.pdf",Encoding.ASCII.GetBytes("%PDF-1.4\nlocal fixture")));
        var unknown=await Assets().ImportAsync(LRef(owner),await SourceFile("data.custom",[0,1,2,3]));Assert.Equal("application/pdf",pdf.Asset.MediaType);Assert.Equal("application/octet-stream",unknown.Asset.MediaType);Assert.Null(pdf.Asset.PixelWidth);
    }
    [Fact]
    public async Task AssetIntegrityReportsMissingAndChangedFiles()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile(bytes:[1,2,3]));await File.WriteAllBytesAsync(AssetPath(asset.Asset),[1,2,4]);Assert.Contains("hash",Assert.Single(await Assets().CheckIntegrityAsync(profile)).Reason);
        File.Delete(AssetPath(asset.Asset));Assert.Contains("missing",Assert.Single(await Assets().CheckIntegrityAsync(profile)).Reason);
    }
    [Fact]
    public async Task AssetAndRichContentRejectForeignProfilesAndResolveAfterSwitchBack()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());var doc=await Rich().CreateAsync(LRef(owner),"**Private**");
        var other=await profiles.CreateAsync("Other content");var otherOwner=await Lists().CreateAsync(other.Id,new("Other"));
        await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Assets().GetAsync(Key(asset.Asset.Id)));await Assert.ThrowsAsync<WorkspaceChangedException>(()=>Rich().GetAsync(Key(doc.Document.Id)));
        await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().AddReferenceAsync(new(other.Id,otherOwner.Item.Id),Key(asset.Asset.Id)));
        await Assert.ThrowsAsync<ContentValidationException>(()=>Rich().AddReferenceAsync(new(other.Id,otherOwner.Item.Id),Key(doc.Document.Id)));
        await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().GetAsync(new(other.Id,asset.Asset.Id)));await Assert.ThrowsAsync<ContentValidationException>(()=>Rich().GetAsync(new(other.Id,doc.Document.Id)));
        await profiles.SwitchAsync(profile);Assert.Equal(asset.Asset,await Assets().GetAsync(Key(asset.Asset.Id)));Assert.Equal(doc.Document,await Rich().GetAsync(Key(doc.Document.Id)));
    }
    [Fact]
    public async Task RichDocumentCanonicalVersionPlainTextLinksAndStableUpdatePersist()
    {
        var owner=await List();var uri=$"personalworkspace://item/{owner.Item.Id:D}";var content=$"# Heading\n\n**bold** and *italic*  \nnext line\n\n- first\n- second\n\n1. one\n2. two\n\n[Web](https://example.com) and [Item]({uri})";
        var created=await Rich().CreateAsync(LRef(owner),content);Assert.Equal(content,created.Document.Content);Assert.Equal("markdown-subset",created.Document.Format);Assert.Equal(1,created.Document.FormatVersion);
        Assert.Equal("Heading\n\nbold and italic\nnext line\n\n- first\n- second\n\n1. one\n2. two\n\nWeb and Item",Rich().ExtractPlainText(created.Document));
        Assert.Equal(created.Document,await Rich().UpdateAsync(Key(created.Reference.Id),content));var updated=await Rich().UpdateAsync(Key(created.Reference.Id),content+"\n\nMore");
        Assert.Equal(created.Document.Id,updated.Id);Assert.Equal(created.Document.CreatedAtUtc,updated.CreatedAtUtc);Assert.True(updated.UpdatedAtUtc>created.Document.UpdatedAtUtc);Assert.Equal(updated,await Rich().GetAsync(Key(updated.Id)));
        Assert.Contains(uri,updated.Content);Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM pragma_table_info('RichTextDocuments') WHERE name='PlainText';"));
    }
    [Theory]
    [InlineData("<script>alert(1)</script>")][InlineData("text <b>html</b>")][InlineData("[bad](javascript:alert)")][InlineData("[bad](file:///C:/secret)")][InlineData("[bad](../file)")][InlineData("![remote](https://example.com/image.png)")][InlineData("```\ncode\n```")][InlineData("[bad](personalworkspace://item/not-a-guid)")]
    public async Task RichDocumentRejectsUnsupportedOrUnsafeMarkup(string content)
    {
        var owner=await List();await Assert.ThrowsAsync<ContentValidationException>(()=>Rich().CreateAsync(LRef(owner),content));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM RichTextDocuments;"));
    }
    [Fact]
    public async Task RichPlainTextDerivesEscapesEntitiesAndNormalizedLineEndings()
    {
        var owner=await List();var doc=await Rich().CreateAsync(LRef(owner),"# Title\r\n\r\n\\*literal\\* &amp; **bold _nested_**\r\nline");
        Assert.DoesNotContain('\r',doc.Document.Content);Assert.Equal("Title\n\n*literal* & bold nested\nline",Rich().ExtractPlainText(doc.Document));
        Assert.Throws<ContentValidationException>(()=>Rich().ExtractPlainText(doc.Document with{FormatVersion=99}));
    }
    [Fact]
    public async Task RichSharedReferenceLifecycleAndLastRemovalDeleteOnlyUnownedDocuments()
    {
        var a=await List();var b=await List("Other");var document=await Rich().CreateAsync(LRef(a),"Original");var link=await Rich().AddReferenceAsync(LRef(b),Key(document.Document.Id));
        await Lists().ApplyAsync(LRef(a),ListAction.Archive);Assert.Equal(document,Assert.Single(await Rich().ListAsync(LRef(a))));await Assert.ThrowsAsync<ContentValidationException>(()=>Rich().UpdateAsync(Key(document.Reference.Id),"No"));
        await Lists().ApplyAsync(LRef(a),ListAction.RestoreArchive);await Lists().ApplyAsync(LRef(a),ListAction.Trash);await Lists().ApplyAsync(LRef(a),ListAction.RestoreTrash);
        await Rich().UpdateAsync(Key(link.Id),"Shared");Assert.Equal("Shared",(await Rich().GetAsync(Key(document.Document.Id))).Content);
        await Lists().ApplyAsync(LRef(a),ListAction.Trash);await Lists().DeleteAsync(LRef(a));Assert.Equal("Shared",(await Rich().GetAsync(Key(document.Document.Id))).Content);
        await Rich().RemoveReferenceAsync(Key(link.Id));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM RichTextDocuments;"));Assert.Null(await Sql("PRAGMA foreign_key_check;"));
    }
    [Fact]
    public async Task RichCreateFailureRollsBackDocumentAndReference()
    {
        var owner=await List();await Sql("CREATE TRIGGER FailRich BEFORE INSERT ON RichTextReferences BEGIN SELECT RAISE(ABORT,'test'); END;");await Assert.ThrowsAsync<ContentOperationException>(()=>Rich().CreateAsync(LRef(owner),"content"));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM RichTextDocuments;"));
    }
    [Fact]
    public async Task AssetProfilePermanentDeletionRemovesPhysicalStorage()
    {
        var owner=await List();await Assets().ImportAsync(LRef(owner),await SourceFile());await Rich().CreateAsync(LRef(owner),"content");var folder=paths.ProfileDirectory(profile);
        await profiles.DeleteAsync(profile);Assert.False(Directory.Exists(folder));Assert.Null(current.Current);
    }
    [Fact]
    public void AssetStorageRejectsJunctionsWithoutTouchingExternalFiles()
    {
        var id=Guid.NewGuid();var relative=AssetPaths.Relative(id,"notes.txt");var shard=Path.Combine(paths.ProfileAttachments(profile),relative[..2]);var outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);
        var start=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("/c");start.ArgumentList.Add("mklink");start.ArgumentList.Add("/J");start.ArgumentList.Add(shard);start.ArgumentList.Add(outside);
        using var process=System.Diagnostics.Process.Start(start)!;process.WaitForExit();Assert.Equal(0,process.ExitCode);
        try { Assert.Throws<ContentValidationException>(()=>new AssetPaths(paths).Resolve(profile,relative));Assert.Throws<ContentValidationException>(()=>AssetFiles().Delete(profile,relative));Assert.True(Directory.Exists(outside)); }
        finally { Directory.Delete(shard); }
    }
    [Fact]
    public async Task AssetJpegContentMetadataAndTruncationValidation()
    {
        // Three-by-two JPEG emitted by the native Windows encoder; no external fixture/download.
        var bytes=Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8qqKKKAP/2Q==");
        var owner=await List();var imported=await Assets().ImportAsync(LRef(owner),await SourceFile("photo.jpg",bytes));Assert.Equal("image/jpeg",imported.Asset.MediaType);Assert.Equal(3,imported.Asset.PixelWidth);Assert.Equal(2,imported.Asset.PixelHeight);
        var truncated=await SourceFile("truncated.jpg",bytes[..^2]);await Assert.ThrowsAsync<ContentValidationException>(()=>Assets().ImportAsync(LRef(owner),truncated));Assert.Single(StoredFiles());
    }
    [Fact]
    public async Task AssetQueuedCleanupRunsDuringProfileOpenWithoutScanningUnknownFiles()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());await Lists().ApplyAsync(LRef(owner),ListAction.Trash);await Lists().DeleteAsync(LRef(owner));
        var unknown=Path.Combine(paths.ProfileAttachments(profile),"unmanaged.txt");await File.WriteAllTextAsync(unknown,"Never delete unknown files");
        var connection=new SqliteConnectionFactory(paths);using var withMaintenance=new ProfileService(new SqliteProfileRepository(connection),new SqliteSettingsService(connection,NullLogger<SqliteSettingsService>.Instance),new ProfileFiles(paths),initializer,current,NullLogger<ProfileService>.Instance,gate,Assets());
        await withMaintenance.SwitchAsync(profile);Assert.False(File.Exists(AssetPath(asset.Asset)));Assert.True(File.Exists(unknown));Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Fact]
    public async Task AssetAndRichOwnerDeletionRollbackPreservesReferencesAndFiles()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());var rich=await Rich().CreateAsync(LRef(owner),"Keep");await Lists().ApplyAsync(LRef(owner),ListAction.Trash);
        await Sql("CREATE TRIGGER FailOwnerDelete BEFORE DELETE ON Lists BEGIN SELECT RAISE(ABORT,'test'); END;");await Assert.ThrowsAsync<ListOperationException>(()=>Lists().DeleteAsync(LRef(owner)));
        Assert.Equal(asset,Assert.Single(await Assets().ListAsync(LRef(owner))));Assert.Equal(rich,Assert.Single(await Rich().ListAsync(LRef(owner))));Assert.True(File.Exists(AssetPath(asset.Asset)));Assert.Empty((await Assets().CleanupAsync(profile)).Pending);Assert.True(File.Exists(AssetPath(asset.Asset)));
    }
    [Fact]
    public async Task AssetDatabaseGuardsIdentityAndReferencedDeletion()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());
        foreach(var sql in new[]{"UPDATE Assets SET StorageRelativePath='../escape';","UPDATE Assets SET PendingDelete=1;","DELETE FROM Assets;","UPDATE AssetReferences SET OwnerItemId='foreign';"})await Assert.ThrowsAsync<SqliteException>(()=>Sql(sql));
        Assert.Equal(asset.Asset,await Assets().GetAsync(Key(asset.Asset.Id)));Assert.True(File.Exists(AssetPath(asset.Asset)));
    }
    [Fact]
    public async Task AssetProfileSwitchWaitsForStreamingImport()
    {
        var other=await profiles.CreateAsync("Other streaming");await profiles.SwitchAsync(profile);var owner=await List();var file=await SourceFile();var delayed=new FaultyAssetStore(AssetFiles()){Pause=true};
        var importing=Assets(delayed).ImportAsync(LRef(owner),file);await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var switching=profiles.SwitchAsync(other.Id);Assert.False(switching.IsCompleted);
        delayed.Release.SetResult();var result=await importing;await switching;Assert.Equal(other.Id,current.Current!.Id);await profiles.SwitchAsync(profile);Assert.Equal(result.Asset,await Assets().GetAsync(Key(result.Asset.Id)));
    }
    [Fact]
    public async Task AssetCancelledImportAfterJournalCommitCleansRecoveryRecord()
    {
        var owner=await List();var file=await SourceFile();var delayed=new FaultyAssetStore(AssetFiles()){Pause=true};using var cancel=new CancellationTokenSource();
        var importing=Assets(delayed).ImportAsync(LRef(owner),file,cancel.Token);await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>importing);
        Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM AssetImports;"));Assert.Empty(StoredFiles());Assert.Equal(0L,await Sql("SELECT COUNT(*) FROM Assets;"));
    }
    [Fact]
    public async Task AssetAndDocumentSurviveRelocatingWholeLocalDataDirectory()
    {
        var owner=await List();var asset=await Assets().ImportAsync(LRef(owner),await SourceFile());var document=await Rich().CreateAsync(LRef(owner),"# Portable");
        var moved=new ApplicationPaths(Path.Combine(root,"relocated"));var from=Path.GetFullPath(paths.Root);var to=Path.GetFullPath(moved.Root);
        Assert.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,from,StringComparison.OrdinalIgnoreCase);Assert.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,to,StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);Directory.Move(from,to);
        var state=new CurrentProfile(moved);var connection=new SqliteConnectionFactory(moved);var assetService=new AssetService(new SqliteContentRepository(),new AssetFileStore(new AssetPaths(moved)),moved,state,gate,clock,NullLogger<AssetService>.Instance);
        using var reopened=new ProfileService(new SqliteProfileRepository(connection),new SqliteSettingsService(connection,NullLogger<SqliteSettingsService>.Instance),new ProfileFiles(moved),new WorkspaceInitializer(moved,NullLogger<WorkspaceInitializer>.Instance),state,NullLogger<ProfileService>.Instance,gate,assetService);
        await reopened.RestoreAsync();Assert.Equal(asset.Asset,await assetService.GetAsync(Key(asset.Asset.Id)));Assert.Empty(await assetService.CheckIntegrityAsync(profile));
        var rich=new RichContentService(new SqliteContentRepository(),state,gate,clock,NullLogger<RichContentService>.Instance);Assert.Equal(document.Document,await rich.GetAsync(Key(document.Document.Id)));
    }
    private sealed class FaultyAssetStore(IAssetFileStore inner) : IAssetFileStore
    {
        public bool FailDelete { get; set; }
        public bool FailAfterImport { get; set; }
        public bool Pause { get; set; }
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CheckImportDestination(Guid profile,AssetImportJob job)=>inner.CheckImportDestination(profile,job);
        public async Task<AssetFileInfo> ImportAsync(Guid profile,AssetImportJob job,string source,CancellationToken token){if(Pause){Started.SetResult();await Release.Task.WaitAsync(token);}var result=await inner.ImportAsync(profile,job,source,token);if(FailAfterImport)throw new IOException("Simulated import failure");return result;}
        public void Delete(Guid profile,string path){if(FailDelete)throw new IOException("Simulated locked file");inner.Delete(profile,path);}
        public Task CopyToAsync(Guid profile,Asset asset,Stream destination,CancellationToken token)=>inner.CopyToAsync(profile,asset,destination,token);
        public Task<string?> CheckIntegrityAsync(Guid profile,Asset asset,CancellationToken token)=>inner.CheckIntegrityAsync(profile,asset,token);
    }
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; } public int MaxWrite { get; private set; }
        public override void Write(byte[] buffer,int offset,int count){Count+=count;MaxWrite=Math.Max(MaxWrite,count);}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default){Count+=buffer.Length;MaxWrite=Math.Max(MaxWrite,buffer.Length);return ValueTask.CompletedTask;}
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>Count;public override long Position{get=>Count;set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
    }
}
