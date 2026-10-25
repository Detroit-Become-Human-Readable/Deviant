using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed class StagedLocalizationGeneration : IDisposable
{
    internal StagedLocalizationGeneration(FeatureStore owner, string recordStoreVersion, string searchProjectionVersion,
        string generation, string? basePublishedGeneration, Guid writerToken)
    { Owner=owner; RecordStoreVersion=recordStoreVersion; SearchProjectionVersion=searchProjectionVersion; Generation=generation; BasePublishedGeneration=basePublishedGeneration; WriterToken=writerToken; }
    internal FeatureStore Owner { get; }
    internal Guid WriterToken { get; }
    internal bool Active { get; set; } = true;
    public bool IsActive => Active;
    public string RecordStoreVersion { get; }
    public string SearchProjectionVersion { get; }
    public string Generation { get; }
    public string? BasePublishedGeneration { get; }
    public void Dispose() => Owner.Abandon(this);
}

public sealed class PublishedLocalizationGeneration<THead> : IDisposable
{
    internal PublishedLocalizationGeneration(FeatureStore owner, string version, string generation, THead head,
        LocalizationGenerationIdentity identity, bool searchProjectionMatches)
    { Owner=owner; RecordStoreVersion=version; Generation=generation; Head=head; Identity=identity; SearchProjectionMatches=searchProjectionMatches; }
    internal FeatureStore Owner { get; }
    internal bool Active { get; set; } = true;
    public bool IsActive => Active;
    public string RecordStoreVersion { get; }
    public string Generation { get; }
    public THead Head { get; }
    public LocalizationGenerationIdentity Identity { get; }
    public bool SearchProjectionMatches { get; }
    public void Dispose() => Owner.Release(this);
}

public sealed record LocalizationGenerationRow<TRecord>(string IdentityKey, TRecord Record, AssetDescriptor SearchAsset);
public sealed record LocalizationGenerationIdentity(string InputSnapshotId, string ParsedResultId, string RecordStoreVersion,
    string SearchProjectionVersion, string JsonPolicyVersion, long RecordCount);
public sealed record LocalizationGenerationRecord<TRecord>(long Sequence, string IdentityKey, TRecord Record);
public sealed record LocalizationGenerationRecordPage<TRecord>(IReadOnlyList<LocalizationGenerationRecord<TRecord>> Items, long NextSequence, bool HasMore);
public sealed record LocalizationGenerationQueryPage<TRecord>(long Total, long Offset, long NextOffset, IReadOnlyList<TRecord> Records);

public sealed partial class FeatureStore
{
    public const int LocalizationGenerationBatchLimit = 512;
    public const int LocalizationGenerationPageLimit = 2048;
    public const int LocalizationGenerationCallerLimit = 10000;
    public const string LocalizationRecordsCollection = "records";
    public const string LocalizationSearchCollection = "search-assets";
    public const string LocalizationFeature = "localization.snapshot";
    private const int MaxGenerationRowBytes = 64 * 1024 * 1024;
    private const int MaxGenerationBatchBytes = 128 * 1024 * 1024;
    private const int MaxGenerationHeadBytes = 4 *  1024 * 1024;
    private const int MaxGenerationBatches = 100_000;
    private const long MaxGenerationTotalBytes = 4L * 1024 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record SearchProjection(string Family,string Label,string Search,string Fold,string SortKey,
        string? GroupKey,string? Language,string? Codec,double? Duration,int? Channels,int Availability,
        int Completeness,string? LookupKey);
    private sealed record PreparedRow(string Key,string RecordJson,string AssetJson,SearchProjection Projection);
    private sealed record BatchManifest(long Batch,long First,long Count,long RecordBytes,string RecordHash,string RecordKeyHash,
        long SearchBytes,string SearchHash,string SearchKeyHash);
    private sealed record WriterState(string State,long NextSequence,long BatchCount,long TotalBytes);
    private sealed record StoredHeader<T>(T Head,string SearchVersion,string InputId,string ResultId,
        string JsonVersion,long RecordCount,long AssetCount,long BatchCount);

    internal void InitializeLocalizationStreamedGroups()
    {
        using var command=connection.CreateCommand();
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS localization_generations(
              revision TEXT NOT NULL,generation TEXT NOT NULL,record_store_version TEXT NOT NULL,
              search_projection_version TEXT NOT NULL,base_generation TEXT,writer_token TEXT NOT NULL,
              state TEXT NOT NULL,head_payload TEXT,head_sha256 TEXT,input_snapshot_id TEXT,parsed_result_id TEXT,json_policy_version TEXT,
              record_count INTEGER NOT NULL DEFAULT 0,asset_count INTEGER NOT NULL DEFAULT 0,total_bytes INTEGER NOT NULL DEFAULT 0,
              next_sequence INTEGER NOT NULL DEFAULT 0,batch_count INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(revision,generation));
            CREATE TABLE IF NOT EXISTS localization_generation_active(
              revision TEXT NOT NULL,record_store_version TEXT NOT NULL,generation TEXT NOT NULL,
              PRIMARY KEY(revision,record_store_version));
            CREATE TABLE IF NOT EXISTS localization_generation_batches(
              revision TEXT NOT NULL,generation TEXT NOT NULL,batch INTEGER NOT NULL,first_sequence INTEGER NOT NULL,
              row_count INTEGER NOT NULL,record_bytes INTEGER NOT NULL,record_hash TEXT NOT NULL,record_key_hash TEXT NOT NULL,
              search_bytes INTEGER NOT NULL,search_hash TEXT NOT NULL,search_key_hash TEXT NOT NULL,
              PRIMARY KEY(revision,generation,batch));
            CREATE TABLE IF NOT EXISTS localization_generation_rows(
              row_id INTEGER PRIMARY KEY,revision TEXT NOT NULL,generation TEXT NOT NULL,collection TEXT NOT NULL,
              sequence INTEGER NOT NULL,item_key TEXT NOT NULL,payload TEXT NOT NULL,family TEXT,label TEXT,search TEXT,
              search_fold TEXT,sort_key TEXT,group_key TEXT,language TEXT,codec TEXT,duration REAL,channels INTEGER,
              availability INTEGER,completeness INTEGER,lookup_key TEXT,
              UNIQUE(revision,generation,collection,sequence),UNIQUE(revision,generation,collection,item_key));
            CREATE INDEX IF NOT EXISTS loc_gen_page ON localization_generation_rows(revision,generation,collection,sequence);
            CREATE INDEX IF NOT EXISTS loc_gen_key ON localization_generation_rows(revision,generation,collection,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_sort ON localization_generation_rows(revision,generation,collection,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_group ON localization_generation_rows(revision,generation,collection,group_key,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_family ON localization_generation_rows(revision,generation,collection,family,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_language ON localization_generation_rows(revision,generation,collection,language,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_codec ON localization_generation_rows(revision,generation,collection,codec,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_lookup ON localization_generation_rows(revision,generation,collection,lookup_key,group_key,sort_key,item_key);
            CREATE INDEX IF NOT EXISTS loc_gen_name ON localization_generation_rows(revision,generation,collection,deviant_natural(label),sort_key,item_key) WHERE collection='search-assets';
            CREATE VIRTUAL TABLE IF NOT EXISTS localization_generation_fts
              USING fts5(search_fold,content='localization_generation_rows',content_rowid='row_id',tokenize='trigram case_sensitive 1');
            CREATE TRIGGER IF NOT EXISTS loc_gen_ai AFTER INSERT ON localization_generation_rows
              WHEN new.collection='search-assets' AND new.search_fold IS NOT NULL BEGIN
                INSERT INTO localization_generation_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS loc_gen_ad AFTER DELETE ON localization_generation_rows
              WHEN old.collection='search-assets' AND old.search_fold IS NOT NULL BEGIN
                INSERT INTO localization_generation_fts(localization_generation_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS loc_gen_au AFTER UPDATE OF search_fold ON localization_generation_rows
              WHEN old.collection='search-assets' AND old.search_fold IS NOT new.search_fold BEGIN
                INSERT INTO localization_generation_fts(localization_generation_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold);
                INSERT INTO localization_generation_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            """;
        command.ExecuteNonQuery();
    }

    public StagedLocalizationGeneration BeginLocalizationGeneration(string recordStoreVersion,string searchProjectionVersion,
        CancellationToken token=default)
    {
        ValidateVersion(recordStoreVersion,nameof(recordStoreVersion)); ValidateVersion(searchProjectionVersion,nameof(searchProjectionVersion));
        var id=Guid.NewGuid().ToString("N"); var writerToken=Guid.NewGuid(); string? baseGeneration=null;
        lock(gate) CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction();
            using(var q=connection.CreateCommand())
            { q.Transaction=tx;q.CommandText="SELECT generation FROM localization_generation_active WHERE revision=$r AND record_store_version=$v";
              q.Parameters.AddWithValue("$r",revision);q.Parameters.AddWithValue("$v",recordStoreVersion);baseGeneration=q.ExecuteScalar() as string; }
            using(var ins=connection.CreateCommand())
            { ins.Transaction=tx;ins.CommandText="INSERT INTO localization_generations(revision,generation,record_store_version,search_projection_version,base_generation,writer_token,state) VALUES($r,$g,$v,$s,$b,$t,'staging')";
              ins.Parameters.AddWithValue("$r",revision);ins.Parameters.AddWithValue("$g",id);ins.Parameters.AddWithValue("$v",recordStoreVersion);
              ins.Parameters.AddWithValue("$s",searchProjectionVersion);ins.Parameters.AddWithValue("$b",(object?)baseGeneration??DBNull.Value);
              ins.Parameters.AddWithValue("$t",writerToken.ToString("N"));ins.ExecuteNonQuery(); }
            tx.Commit();return true;
        });
        return new(this,recordStoreVersion,searchProjectionVersion,id,baseGeneration,writerToken);
    }

    private static void ValidateVersion(string value,string name)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value);if(value.Length>256||StrictUtf8.GetByteCount(value)>1024)throw new ArgumentOutOfRangeException(name); }
    public void AppendLocalizationGeneration<TRecord>(StagedLocalizationGeneration generation,
        IReadOnlyList<LocalizationGenerationRow<TRecord>> rows,CancellationToken token=default)
    {
        ValidateWriter(generation);ArgumentNullException.ThrowIfNull(rows);
        if(rows.Count is <1 or >LocalizationGenerationBatchLimit)throw new ArgumentOutOfRangeException(nameof(rows));
        var prepared=new PreparedRow[rows.Count];long bytes=0;
        using var rh=NewRowHash("records",generation.RecordStoreVersion);
        using var sh=NewRowHash("search-assets",generation.SearchProjectionVersion);
        using var rkh=NewKeyHash(generation.RecordStoreVersion);using var skh=NewKeyHash(generation.RecordStoreVersion);
        for(var i=0;i<rows.Count;i++)
        {
            token.ThrowIfCancellationRequested();var row=rows[i]??throw new InvalidDataException("A generation row is null.");
            ValidateKey(row.IdentityKey);ArgumentNullException.ThrowIfNull(row.Record);ArgumentNullException.ThrowIfNull(row.SearchAsset);
            if(!string.Equals(row.IdentityKey,row.SearchAsset.Identity.Key,StringComparison.Ordinal))
                throw new InvalidDataException("Record and descriptor identities differ.");
            var record=BoundedFeatureJson.Serialize(row.Record,MaxGenerationRowBytes);
            var asset=BoundedFeatureJson.Serialize(row.SearchAsset,MaxGenerationRowBytes);
            var projection=Project(row.SearchAsset);
            var projectionBytes=ProjectionByteCount(projection);
            var rowBytes=checked(StrictUtf8.GetByteCount(record)+StrictUtf8.GetByteCount(asset)+projectionBytes);
            if(rowBytes>MaxGenerationRowBytes)throw new InvalidDataException("Generation record and search projection exceed their row byte limit.");
            if(rowBytes>MaxGenerationBatchBytes-bytes)throw new InvalidDataException("Append batch exceeds its byte limit.");
            bytes+=rowBytes;AppendKey(rkh,row.IdentityKey);AppendKey(skh,row.SearchAsset.Identity.Key);AppendRecord(rh,i,row.IdentityKey,record);
            AppendSearch(sh,i,row.IdentityKey,asset,projection);prepared[i]=new(row.IdentityKey,record,asset,projection);
        }
        var recordHash=Finish(rh);var searchHash=Finish(sh);var recordKeyHash=Finish(rkh);var searchKeyHash=Finish(skh);
        var recordBytes=prepared.Sum(x=>(long)StrictUtf8.GetByteCount(x.RecordJson));
        var searchBytes=prepared.Sum(x=>(long)StrictUtf8.GetByteCount(x.AssetJson)+ProjectionByteCount(x.Projection));var committed=false;
        lock(gate)try{CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction();var state=ReadWriterState(tx,generation);
            if(state.State!="staging")throw new InvalidOperationException("Generation is no longer staged.");
            if(state.BatchCount>=MaxGenerationBatches)throw new InvalidDataException("Generation has too many batches.");
            if(bytes>MaxGenerationTotalBytes-state.TotalBytes)throw new InvalidDataException("Generation exceeds its total byte limit.");
            for(var i=0;i<prepared.Length;i++)
            {
                token.ThrowIfCancellationRequested();var sequence=checked(state.NextSequence+i);
                InsertRow(tx,generation,LocalizationRecordsCollection,sequence,prepared[i].Key,prepared[i].RecordJson,null);
                InsertRow(tx,generation,LocalizationSearchCollection,sequence,prepared[i].Key,prepared[i].AssetJson,prepared[i].Projection);
            }
            using(var batch=connection.CreateCommand())
            {
                batch.Transaction=tx;batch.CommandText="INSERT INTO localization_generation_batches VALUES($r,$g,$b,$f,$n,$rb,$rh,$rkh,$sb,$sh,$skh)";
                batch.Parameters.AddWithValue("$r",revision);batch.Parameters.AddWithValue("$g",generation.Generation);
                batch.Parameters.AddWithValue("$b",state.BatchCount);batch.Parameters.AddWithValue("$f",state.NextSequence);
                batch.Parameters.AddWithValue("$n",prepared.Length);batch.Parameters.AddWithValue("$rb",recordBytes);
                batch.Parameters.AddWithValue("$rh",recordHash);batch.Parameters.AddWithValue("$rkh",recordKeyHash);batch.Parameters.AddWithValue("$sb",searchBytes);
                batch.Parameters.AddWithValue("$sh",searchHash);batch.Parameters.AddWithValue("$skh",searchKeyHash);batch.ExecuteNonQuery();
            }
            using(var update=connection.CreateCommand())
            {
                update.Transaction=tx;update.CommandText="UPDATE localization_generations SET record_count=record_count+$n,asset_count=asset_count+$n,next_sequence=next_sequence+$n,batch_count=batch_count+1,total_bytes=total_bytes+$bytes WHERE revision=$r AND generation=$g AND state='staging' AND writer_token=$t";
                update.Parameters.AddWithValue("$n",prepared.Length);update.Parameters.AddWithValue("$r",revision);
                update.Parameters.AddWithValue("$bytes",bytes);
                update.Parameters.AddWithValue("$g",generation.Generation);update.Parameters.AddWithValue("$t",generation.WriterToken.ToString("N"));
                if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Generation writer was superseded.");
            }
            token.ThrowIfCancellationRequested();tx.Commit();committed=true;return true;
        });}catch(OperationCanceledException)when(committed){ }
    }

    public PublishedLocalizationGeneration<THead> PublishLocalizationGeneration<THead>(StagedLocalizationGeneration generation,THead head,
        Func<THead,LocalizationGenerationIdentity> identify,CancellationToken token=default)
    {
        ValidateWriter(generation);ArgumentNullException.ThrowIfNull(head);ArgumentNullException.ThrowIfNull(identify);
        var identity=identify(head)??throw new InvalidDataException("Generation identity is missing.");
        ValidateIdentity(identity,generation);var payload=BoundedFeatureJson.Serialize(head,MaxGenerationHeadBytes);var committed=false;
        SealLocalizationGeneration(generation,identity,token);
        ValidateStagedLocalizationRows(generation,token);
        lock(gate)try{CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction();var state=ReadWriterState(tx,generation);
            if(state.State!="validating")throw new InvalidOperationException("Generation validation was superseded.");
            string? active;
            using(var q=connection.CreateCommand())
            { q.Transaction=tx;q.CommandText="SELECT generation FROM localization_generation_active WHERE revision=$r AND record_store_version=$v";
              q.Parameters.AddWithValue("$r",revision);q.Parameters.AddWithValue("$v",generation.RecordStoreVersion);active=q.ExecuteScalar() as string; }
            if(!string.Equals(active,generation.BasePublishedGeneration,StringComparison.Ordinal))
                throw new InvalidOperationException("A newer localization generation was published first.");
            ValidateBatches(ReadBatches(tx,generation.Generation),state);token.ThrowIfCancellationRequested();
            using(var publish=connection.CreateCommand())
            {
                publish.Transaction=tx;publish.CommandText="UPDATE localization_generations SET state='published',head_payload=$h,head_sha256=$hh,input_snapshot_id=$i,parsed_result_id=$p,json_policy_version=$j WHERE revision=$r AND generation=$g AND state='validating' AND writer_token=$t";
                publish.Parameters.AddWithValue("$h",payload);publish.Parameters.AddWithValue("$hh",HashHead(payload));publish.Parameters.AddWithValue("$i",identity.InputSnapshotId);
                publish.Parameters.AddWithValue("$p",identity.ParsedResultId);publish.Parameters.AddWithValue("$j",identity.JsonPolicyVersion);
                publish.Parameters.AddWithValue("$r",revision);publish.Parameters.AddWithValue("$g",generation.Generation);
                publish.Parameters.AddWithValue("$t",generation.WriterToken.ToString("N"));
                if(publish.ExecuteNonQuery()!=1)throw new InvalidOperationException("Generation could not be published.");
            }
            using(var activeUpdate=connection.CreateCommand())
            { activeUpdate.Transaction=tx;activeUpdate.CommandText="INSERT INTO localization_generation_active VALUES($r,$v,$g) ON CONFLICT(revision,record_store_version) DO UPDATE SET generation=excluded.generation";
              activeUpdate.Parameters.AddWithValue("$r",revision);activeUpdate.Parameters.AddWithValue("$v",generation.RecordStoreVersion);
              activeUpdate.Parameters.AddWithValue("$g",generation.Generation);activeUpdate.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested();tx.Commit();committed=true;return true;
        });}catch(OperationCanceledException)when(committed){ }
        generation.Active=false;
        return new(this,generation.RecordStoreVersion,generation.Generation,head,identity,true);
    }

    private void SealLocalizationGeneration(StagedLocalizationGeneration generation,
        LocalizationGenerationIdentity identity,CancellationToken token)
    {
        lock(gate)CatalogService.ExecuteInterruptible(connection,token,() =>
        {
            using var tx=connection.BeginTransaction();var state=ReadWriterState(tx,generation);
            if(state.State!="staging")throw new InvalidOperationException("Generation is no longer staged.");
            if(identity.RecordCount!=state.NextSequence)throw new InvalidDataException("Head record count differs from staged rows.");
            string? current;
            using(var q=connection.CreateCommand())
            { q.Transaction=tx;q.CommandText="SELECT generation FROM localization_generation_active WHERE revision=$r AND record_store_version=$v";
              q.Parameters.AddWithValue("$r",revision);q.Parameters.AddWithValue("$v",generation.RecordStoreVersion);current=q.ExecuteScalar() as string; }
            if(!string.Equals(current,generation.BasePublishedGeneration,StringComparison.Ordinal))
                throw new InvalidOperationException("A newer localization generation was published first.");
            ValidateBatches(ReadBatches(tx,generation.Generation),state);
            using(var update=connection.CreateCommand())
            { update.Transaction=tx;update.CommandText="UPDATE localization_generations SET state='validating' WHERE revision=$r AND generation=$g AND state='staging' AND writer_token=$t";
              update.Parameters.AddWithValue("$r",revision);update.Parameters.AddWithValue("$g",generation.Generation);
              update.Parameters.AddWithValue("$t",generation.WriterToken.ToString("N"));
              if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Generation could not be sealed."); }
            tx.Commit();return true;
        });
    }

    private void ValidateStagedLocalizationRows(StagedLocalizationGeneration generation,CancellationToken token)
    {
        lock(gate)CatalogService.ExecuteInterruptible(connection,token,() =>
        {
            using var tx=connection.BeginTransaction(deferred:true);var state=ReadWriterState(tx,generation);
            if(state.State!="validating")throw new InvalidOperationException("Generation is not sealed for validation.");
            var batches=ReadBatches(tx,generation.Generation);ValidateBatches(batches,state);
            if(batches.Any(batch=>batch.RecordKeyHash!=batch.SearchKeyHash))throw new InvalidDataException("Record and search key sets differ.");
            ValidateCollection(tx,generation.Generation,batches,LocalizationRecordsCollection,false,
                generation.RecordStoreVersion,generation.RecordStoreVersion,token);
            ValidateCollection(tx,generation.Generation,batches,LocalizationSearchCollection,true,
                generation.SearchProjectionVersion,generation.RecordStoreVersion,token);
            token.ThrowIfCancellationRequested();tx.Commit();return true;
        });
    }

    public PublishedLocalizationGeneration<THead>? OpenLocalizationGeneration<THead>(string recordStoreVersion,
        string searchProjectionVersion,Func<THead,LocalizationGenerationIdentity> identify,Func<THead,bool> acceptsSource,
        CancellationToken token=default)
    {
        ValidateVersion(recordStoreVersion,nameof(recordStoreVersion));ValidateVersion(searchProjectionVersion,nameof(searchProjectionVersion));
        ArgumentNullException.ThrowIfNull(identify);ArgumentNullException.ThrowIfNull(acceptsSource);
        try
        {
        lock(gate)return CatalogService.ExecuteInterruptible<PublishedLocalizationGeneration<THead>?>(connection,token,()=>
        {
            using var tx=connection.BeginTransaction(deferred:true);string? generation;
            using(var active=connection.CreateCommand())
            { active.Transaction=tx;active.CommandText="SELECT generation FROM localization_generation_active WHERE revision=$r AND record_store_version=$v";
              active.Parameters.AddWithValue("$r",revision);active.Parameters.AddWithValue("$v",recordStoreVersion);generation=active.ExecuteScalar() as string; }
            if(generation is null)return null;
            var stored=ReadHeader<THead>(tx,generation,recordStoreVersion);if(stored is null)return null;
            var identity=identify(stored.Head);ValidateStoredIdentity(identity,stored,recordStoreVersion);
            if(!acceptsSource(stored.Head))return null;
            var searchMatches=stored.SearchVersion==searchProjectionVersion;
            ValidatePublishedRows(tx,generation,stored,recordStoreVersion,searchMatches,token);
            token.ThrowIfCancellationRequested();tx.Commit();
            return new(this,recordStoreVersion,generation,stored.Head,identity,searchMatches);
        });
        }
        catch(Exception exception) when(exception is JsonException or InvalidDataException or FormatException or OverflowException or InvalidOperationException)
        { token.ThrowIfCancellationRequested();return null; }
    }
    public TRecord? LoadLocalizationRecord<THead,TRecord>(PublishedLocalizationGeneration<THead> generation,
        string identityKey,CancellationToken token=default)
    {
        ValidateLease(generation,true);ValidateKey(identityKey);
        lock(gate)return CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction(deferred:true);ValidatePinnedGeneration(tx,generation);
            using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT payload FROM localization_generation_rows WHERE revision=$r AND generation=$g AND collection=$c AND item_key=$k";
            BindGroup(command,generation.Generation);command.Parameters.AddWithValue("$c",LocalizationRecordsCollection);
            command.Parameters.AddWithValue("$k",identityKey);
            var result=command.ExecuteScalar() is string payload?Deserialize<TRecord>(payload):default;tx.Commit();return result;
        });
    }

    public LocalizationGenerationRecordPage<TRecord> LoadLocalizationRecordPage<THead,TRecord>(
        PublishedLocalizationGeneration<THead> generation,long afterSequence,int limit,CancellationToken token=default)
    {
        ValidateLease(generation,true);ValidatePage(afterSequence,limit);
        lock(gate)return CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction(deferred:true);ValidatePinnedGeneration(tx,generation);
            using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT sequence,item_key,payload FROM localization_generation_rows WHERE revision=$r AND generation=$g AND collection=$c AND sequence>$after ORDER BY sequence LIMIT $limit";
            BindGroup(command,generation.Generation);command.Parameters.AddWithValue("$c",LocalizationRecordsCollection);
            command.Parameters.AddWithValue("$after",afterSequence);command.Parameters.AddWithValue("$limit",limit+1);
            var items=new List<LocalizationGenerationRecord<TRecord>>(limit+1);using var reader=command.ExecuteReader();
            while(reader.Read()){token.ThrowIfCancellationRequested();items.Add(new(reader.GetInt64(0),reader.GetString(1),Deserialize<TRecord>(reader.GetString(2))));}
            var hasMore=items.Count>limit;if(hasMore)items.RemoveAt(items.Count-1);
            var result=new LocalizationGenerationRecordPage<TRecord>(items.AsReadOnly(),items.Count==0?afterSequence:items[^1].Sequence,hasMore);
            tx.Commit();return result;
        });
    }

    public LocalizationGenerationQueryPage<TRecord> QueryLocalizationPage<THead,TRecord>(
        PublishedLocalizationGeneration<THead> generation,AssetQuery query,CancellationToken token=default)
    {
        ValidateLease(generation,false);ValidateQuery(query);return QueryGenerationPage<THead,TRecord>(generation,query,token);
    }

    public LocalizationGenerationQueryPage<TRecord> QueryStagedLocalizationPage<TRecord>(
        StagedLocalizationGeneration generation,AssetQuery query,CancellationToken token=default)
    {
        ValidateWriter(generation);ValidateQuery(query);
        lock(gate) return CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var tx=connection.BeginTransaction(deferred:true);
            if(ReadWriterState(tx,generation).State is not ("staging" or "validating"))
                throw new InvalidOperationException("Generation is no longer staged or validating.");
            var result=QueryGenerationPage<TRecord>(tx,generation.Generation,query,token);tx.Commit();return result;
        });
    }

    public TRecord? LoadStagedLocalizationRecord<TRecord>(StagedLocalizationGeneration generation,
        string identityKey,CancellationToken token=default)
    {
        ValidateWriter(generation);ValidateKey(identityKey);
        lock(gate)return CatalogService.ExecuteInterruptible(connection,token,() =>
        {
            using var tx=connection.BeginTransaction(deferred:true);
            if(ReadWriterState(tx,generation).State is not ("staging" or "validating"))
                throw new InvalidOperationException("Generation is no longer staged or validating.");
            using var command=connection.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT payload FROM localization_generation_rows WHERE revision=$r AND generation=$g AND collection=$c AND item_key=$k";
            BindGroup(command,generation.Generation);command.Parameters.AddWithValue("$c",LocalizationRecordsCollection);
            command.Parameters.AddWithValue("$k",identityKey);
            var result=command.ExecuteScalar() is string payload?Deserialize<TRecord>(payload):default;tx.Commit();return result;
        });
    }

    private LocalizationGenerationQueryPage<TRecord> QueryGenerationPage<THead,TRecord>(PublishedLocalizationGeneration<THead> generation,AssetQuery query,CancellationToken token)
    {
        lock(gate)return CatalogService.ExecuteInterruptible(connection,token,()=>
        { using var tx=connection.BeginTransaction(deferred:true);ValidatePinnedGeneration(tx,generation);
          var page=QueryGenerationPage<TRecord>(tx,generation.Generation,query,token);tx.Commit();return page; });
    }

    private LocalizationGenerationQueryPage<TRecord> QueryGenerationPage<TRecord>(SqliteTransaction tx,string generation,AssetQuery query,CancellationToken token)
    {
        using var count=SearchCommand(tx,generation,query,"COUNT(*)");var total=(long)count.ExecuteScalar()!;
        var records=new List<TRecord>(query.Limit);long offset=query.Offset;var remaining=query.Limit;
        while(remaining>0)
        {
            token.ThrowIfCancellationRequested();var take=Math.Min(LocalizationGenerationPageLimit,remaining);
            using var command=SearchPageCommand(tx,generation,query,take,offset);using var reader=command.ExecuteReader();var read=0;
            while(reader.Read()){token.ThrowIfCancellationRequested();records.Add(Deserialize<TRecord>(reader.GetString(0)));read++;}
            if(read==0)break;offset+=read;remaining-=read;
        }
        return new(total,query.Offset,query.Offset+records.Count,records.AsReadOnly());
    }

    private SqliteCommand SearchCommand(SqliteTransaction tx,string generation,AssetQuery query,string select,bool joinRecords=false)
    {
        var trigram=!string.IsNullOrWhiteSpace(query.Search)&&query.Search.EnumerateRunes().Count()>=3;
        var command=connection.CreateCommand();command.Transaction=tx;
        var indexHint=query.ExactKey is not null?" INDEXED BY loc_gen_lookup":query.GroupKey is not null?" INDEXED BY loc_gen_group":"";
        var from=" FROM localization_generation_rows a"+indexHint;
        if(joinRecords)from+=" JOIN localization_generation_rows r ON r.revision=a.revision AND r.generation=a.generation AND r.collection=$records AND r.item_key=a.item_key";
        command.CommandText=$"SELECT {select}{from} WHERE a.revision=$r AND a.generation=$g AND a.collection=$c";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation);
        command.Parameters.AddWithValue("$c",LocalizationSearchCollection);
        if(joinRecords)command.Parameters.AddWithValue("$records",LocalizationRecordsCollection);
        void Filter(string column,object? value)
        { if(value is null)return;command.CommandText+=" AND a."+column+"=$"+column;command.Parameters.AddWithValue("$"+column,value); }
        if(!string.IsNullOrWhiteSpace(query.Search))
        {
            command.CommandText+=" AND deviant_contains(a.search,$search)";command.Parameters.AddWithValue("$search",query.Search);
            if(trigram){command.CommandText+=" AND a.row_id IN (SELECT rowid FROM localization_generation_fts WHERE localization_generation_fts MATCH $term)";
                command.Parameters.AddWithValue("$term","\""+query.Search.ToUpperInvariant().Replace("\"","\"\"")+"\"");}
        }
        Filter("group_key",query.GroupKey);Filter("language",query.Language);Filter("codec",query.Codec);
        Filter("lookup_key",query.ExactKey);Filter("family",query.Family);Filter("channels",query.Channels);
        Filter("availability",query.Availability is{} a?(int)a:null);Filter("completeness",query.Completeness is{} c?(int)c:null);
        if(query.MinimumDuration is{} min){command.CommandText+=" AND a.duration >= $min";command.Parameters.AddWithValue("$min",min);}
        if(query.MaximumDuration is{} max){command.CommandText+=" AND a.duration <= $max";command.Parameters.AddWithValue("$max",max);}
        return command;
    }

    private SqliteCommand SearchPageCommand(SqliteTransaction tx,string generation,AssetQuery query,int limit,long offset)
    {
        var command=SearchCommand(tx,generation,query,"r.payload",joinRecords:true);
        var order=LocalizationSortOrder(query);
        command.CommandText+=" ORDER BY "+order+" LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit",limit);command.Parameters.AddWithValue("$offset",offset);
        return command;
    }

    private static string LocalizationSortOrder(AssetQuery query)
    {
        var column=query.Sort switch
        {
            AssetSort.Default=>"a.sort_key",AssetSort.Name=>"deviant_natural(a.label)",AssetSort.Language=>"a.language",
            AssetSort.Codec=>"a.codec",AssetSort.Duration=>"a.duration",AssetSort.Channels=>"a.channels",
            AssetSort.Availability=>"a.availability",AssetSort.Completeness=>"a.completeness",
            _=>throw new ArgumentOutOfRangeException(nameof(query.Sort))
        };
        return column+(query.Descending?" DESC":" ASC")+" NULLS LAST,"+(column=="a.sort_key"?"":"a.sort_key ASC,")+ 
            "deviant_ordinal(a.item_key) ASC,deviant_member(a.item_key) ASC,a.item_key ASC";
    }

    internal void Abandon(StagedLocalizationGeneration generation)
    {
        if(!ReferenceEquals(generation.Owner,this)||!generation.Active)return;
        lock(gate)
        {
            try{using var command=connection.CreateCommand();command.CommandText="UPDATE localization_generations SET state='abandoned' WHERE revision=$r AND generation=$g AND state IN ('staging','validating') AND writer_token=$t";
                command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation.Generation);
                command.Parameters.AddWithValue("$t",generation.WriterToken.ToString("N"));command.ExecuteNonQuery();}
            catch(ObjectDisposedException){}
            generation.Active=false;
        }
    }
    internal void Release<THead>(PublishedLocalizationGeneration<THead> generation)
    {if(ReferenceEquals(generation.Owner,this))generation.Active=false;}

    private WriterState ReadWriterState(SqliteTransaction tx,StagedLocalizationGeneration writer)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT state,next_sequence,batch_count,total_bytes FROM localization_generations WHERE revision=$r AND generation=$g AND record_store_version=$v AND writer_token=$t";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",writer.Generation);
        command.Parameters.AddWithValue("$v",writer.RecordStoreVersion);command.Parameters.AddWithValue("$t",writer.WriterToken.ToString("N"));
        using var row=command.ExecuteReader();if(!row.Read())throw new InvalidOperationException("Unknown localization generation writer.");
        return new(row.GetString(0),row.GetInt64(1),row.GetInt64(2),row.GetInt64(3));
    }

    private List<BatchManifest> ReadBatches(SqliteTransaction tx,string generation)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT batch,first_sequence,row_count,record_bytes,record_hash,record_key_hash,search_bytes,search_hash,search_key_hash FROM localization_generation_batches WHERE revision=$r AND generation=$g ORDER BY batch";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation);
        var batches=new List<BatchManifest>();using var reader=command.ExecuteReader();
        while(reader.Read())
        {
            if(batches.Count>=MaxGenerationBatches)throw new InvalidDataException("Generation manifest exceeds its limit.");
            batches.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetString(4),reader.GetString(5),
                reader.GetInt64(6),reader.GetString(7),reader.GetString(8)));
        }
        return batches;
    }

    private static void ValidateBatches(IReadOnlyList<BatchManifest> batches,WriterState state)
    {
        if(batches.Count!=state.BatchCount)throw new InvalidDataException("Generation batch manifest is incomplete.");
        long sequence=0,totalBytes=0;
        for(var i=0;i<batches.Count;i++)
        {
            var b=batches[i];
            if(b.Batch!=i||b.First!=sequence||b.Count is <1 or >LocalizationGenerationBatchLimit||
               b.RecordBytes<0||b.SearchBytes<0||!IsSha256(b.RecordHash)||!IsSha256(b.SearchHash)||
               !IsSha256(b.RecordKeyHash)||!IsSha256(b.SearchKeyHash)||b.RecordKeyHash!=b.SearchKeyHash)
                throw new InvalidDataException("Generation batch manifest is invalid.");
            sequence=checked(sequence+b.Count);
            totalBytes=checked(totalBytes+b.RecordBytes+b.SearchBytes);
            if(totalBytes>MaxGenerationTotalBytes)throw new InvalidDataException("Generation exceeds its total byte limit.");
        }
        if(sequence!=state.NextSequence||totalBytes!=state.TotalBytes)
            throw new InvalidDataException("Generation row totals or record and descriptor key sets differ.");
    }

    private StoredHeader<T>? ReadHeader<T>(SqliteTransaction tx,string generation,string version)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT search_projection_version,head_payload,head_sha256,input_snapshot_id,parsed_result_id,json_policy_version,record_count,asset_count,batch_count FROM localization_generations WHERE revision=$r AND generation=$g AND record_store_version=$v AND state='published'";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation);command.Parameters.AddWithValue("$v",version);
        using var reader=command.ExecuteReader();
        if(!reader.Read()||reader.IsDBNull(1)||reader.IsDBNull(2)||reader.GetInt64(6)!=reader.GetInt64(7)||reader.GetInt64(8)>MaxGenerationBatches)return null;
        var json=reader.GetString(1);if(StrictUtf8.GetByteCount(json)>MaxGenerationHeadBytes)return null;
        if(!string.Equals(HashHead(json),reader.GetString(2),StringComparison.Ordinal))return null;
        var head=JsonSerializer.Deserialize<T>(json);if(head is null)return null;
        return new(head,reader.GetString(0),reader.GetString(3),reader.GetString(4),reader.GetString(5),
            reader.GetInt64(6),reader.GetInt64(7),reader.GetInt64(8));
    }

    private void ValidatePublishedRows<T>(SqliteTransaction tx,string generation,StoredHeader<T> header,
        string recordStoreVersion,bool validateSearch,CancellationToken token)
    {
        var batches=ReadBatches(tx,generation);if(batches.Count!=header.BatchCount)throw new InvalidDataException("Generation batch list differs from its head.");
        if(batches.Any(batch=>batch.RecordKeyHash!=batch.SearchKeyHash))throw new InvalidDataException("Record and search key sets differ.");
        ValidateCollection(tx,generation,batches,LocalizationRecordsCollection,false,recordStoreVersion,recordStoreVersion,token);
        if(validateSearch)ValidateCollection(tx,generation,batches,LocalizationSearchCollection,true,header.SearchVersion,recordStoreVersion,token);
        var expected=batches.Sum(b=>b.Count);
        if(header.RecordCount!=expected||header.AssetCount!=expected)throw new InvalidDataException("Generation row counts differ from its manifests.");
    }

    private void ValidateCollection(SqliteTransaction tx,string generation,IReadOnlyList<BatchManifest> batches,
        string collection,bool search,string rowHashVersion,string keyHashVersion,CancellationToken token)
    {
        using var timing=AssetLoadDiagnostics.Measure(search?"LocalizationSearchValidation":"LocalizationRecordValidation",generation);
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT sequence,item_key,payload,family,label,search,search_fold,sort_key,group_key,language,codec,duration,channels,availability,completeness,lookup_key FROM localization_generation_rows WHERE revision=$r AND generation=$g AND collection=$c ORDER BY sequence";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation);command.Parameters.AddWithValue("$c",collection);
        using var rows=command.ExecuteReader();long seen=0;
        foreach(var batch in batches)
        {
            token.ThrowIfCancellationRequested();using var rowHash=NewRowHash(collection,rowHashVersion);
            using var keys=NewKeyHash(keyHashVersion);long bytes=0;
            for(long index=0;index<batch.Count;index++)
            {
                token.ThrowIfCancellationRequested();if(!rows.Read())throw new InvalidDataException("Generation row is missing.");
                var sequence=rows.GetInt64(0);var key=rows.GetString(1);var payload=rows.GetString(2);
                if(sequence!=batch.First+index||sequence!=seen++)throw new InvalidDataException("Generation sequences are inconsistent.");
                ValidateKey(key);using(JsonDocument.Parse(payload)){}
                var byteCount=StrictUtf8.GetByteCount(payload);if(byteCount>MaxGenerationRowBytes)throw new InvalidDataException("Generation row exceeds its limit.");
                bytes+=byteCount;AppendKey(keys,key);
                if(search)
                {
                    var projection=ReadProjection(rows);var projectionBytes=ProjectionByteCount(projection);
                    if(projectionBytes>MaxGenerationRowBytes||byteCount+projectionBytes>MaxGenerationRowBytes)
                        throw new InvalidDataException("Generation search projection exceeds its row byte limit.");
                    bytes+=projectionBytes;AppendSearch(rowHash,index,key,payload,projection);
                }
                else AppendRecord(rowHash,index,key,payload);
            }
            var actual=Finish(rowHash);var actualKeys=Finish(keys);
            if(actual!=(search?batch.SearchHash:batch.RecordHash)||actualKeys!=(search?batch.SearchKeyHash:batch.RecordKeyHash)||
               bytes!=(search?batch.SearchBytes:batch.RecordBytes))
                throw new InvalidDataException("Generation collection digest validation failed.");
        }
        if(rows.Read())throw new InvalidDataException("Generation has unexpected rows.");
    }

    private void ValidatePinnedGeneration<THead>(SqliteTransaction tx,PublishedLocalizationGeneration<THead> lease)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT state,record_store_version,search_projection_version,input_snapshot_id,parsed_result_id,json_policy_version,record_count,asset_count FROM localization_generations WHERE revision=$r AND generation=$g";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",lease.Generation);
        using var row=command.ExecuteReader();
        if(!row.Read()||row.GetString(0)!="published"||row.GetString(1)!=lease.RecordStoreVersion||
           row.GetString(2)!=lease.Identity.SearchProjectionVersion||row.GetString(3)!=lease.Identity.InputSnapshotId||
           row.GetString(4)!=lease.Identity.ParsedResultId||row.GetString(5)!=lease.Identity.JsonPolicyVersion||
           row.GetInt64(6)!=lease.Identity.RecordCount||row.GetInt64(7)!=lease.Identity.RecordCount)
            throw new InvalidDataException("The pinned localization generation is missing or changed.");
    }

    private static SearchProjection ReadProjection(SqliteDataReader row)=>new(
        row.GetString(3),row.GetString(4),row.GetString(5),row.GetString(6),row.GetString(7),
        row.IsDBNull(8)?null:row.GetString(8),row.IsDBNull(9)?null:row.GetString(9),row.IsDBNull(10)?null:row.GetString(10),
        row.IsDBNull(11)?null:row.GetDouble(11),row.IsDBNull(12)?null:row.GetInt32(12),
        row.IsDBNull(13)?0:row.GetInt32(13),row.IsDBNull(14)?0:row.GetInt32(14),row.IsDBNull(15)?null:row.GetString(15));

    private void InsertRow(SqliteTransaction tx,StagedLocalizationGeneration writer,string collection,long sequence,
        string key,string payload,SearchProjection? p)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="INSERT INTO localization_generation_rows(revision,generation,collection,sequence,item_key,payload,family,label,search,search_fold,sort_key,group_key,language,codec,duration,channels,availability,completeness,lookup_key) VALUES($r,$g,$c,$s,$k,$p,$family,$label,$search,$fold,$sort,$group,$language,$codec,$duration,$channels,$availability,$complete,$lookup)";
        command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",writer.Generation);
        command.Parameters.AddWithValue("$c",collection);command.Parameters.AddWithValue("$s",sequence);
        command.Parameters.AddWithValue("$k",key);command.Parameters.AddWithValue("$p",payload);
        command.Parameters.AddWithValue("$family",(object?)p?.Family??DBNull.Value);
        command.Parameters.AddWithValue("$label",(object?)p?.Label??DBNull.Value);command.Parameters.AddWithValue("$search",(object?)p?.Search??DBNull.Value);
        command.Parameters.AddWithValue("$fold",p?.Fold??string.Empty);command.Parameters.AddWithValue("$sort",(object?)p?.SortKey??DBNull.Value);
        command.Parameters.AddWithValue("$group",(object?)p?.GroupKey??DBNull.Value);command.Parameters.AddWithValue("$language",(object?)p?.Language??DBNull.Value);
        command.Parameters.AddWithValue("$codec",(object?)p?.Codec??DBNull.Value);command.Parameters.AddWithValue("$duration",(object?)p?.Duration??DBNull.Value);
        command.Parameters.AddWithValue("$channels",(object?)p?.Channels??DBNull.Value);command.Parameters.AddWithValue("$availability",(object?)p?.Availability??DBNull.Value);
        command.Parameters.AddWithValue("$complete",(object?)p?.Completeness??DBNull.Value);command.Parameters.AddWithValue("$lookup",(object?)p?.LookupKey??DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static SearchProjection Project(AssetDescriptor asset)
    {
        var search=asset.SearchTerms??string.Join('\n',asset.Names.Select(x=>x.Text).Prepend(asset.Label).Append(asset.Identity.Key));
        return new(asset.Family,asset.Label,search,search.ToUpperInvariant(),asset.SortKey??NaturalOrder.Key(asset.Label),
            asset.GroupKey,asset.Language,asset.Codec,asset.DurationSeconds,asset.Channels,(int)asset.Availability,(int)asset.Completeness,asset.LookupKey);
    }

    private static long ProjectionByteCount(SearchProjection projection) =>
        (long)StrictUtf8.GetByteCount(projection.Family) + StrictUtf8.GetByteCount(projection.Label) +
        StrictUtf8.GetByteCount(projection.Search) + StrictUtf8.GetByteCount(projection.Fold) +
        StrictUtf8.GetByteCount(projection.SortKey) + StrictUtf8.GetByteCount(projection.GroupKey ?? string.Empty) +
        StrictUtf8.GetByteCount(projection.Language ?? string.Empty) + StrictUtf8.GetByteCount(projection.Codec ?? string.Empty) +
        StrictUtf8.GetByteCount(projection.LookupKey ?? string.Empty) +
        StrictUtf8.GetByteCount(projection.Duration?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty) +
        StrictUtf8.GetByteCount(projection.Channels?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    private static IncrementalHash NewRowHash(string collection,string version)
    {
        var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Frame(hash,"localization-generation-rows-v1");Frame(hash,collection);Frame(hash,version);return hash;
    }
    private static IncrementalHash NewKeyHash(string version)
    {
        var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);Frame(hash,"localization-generation-keys-v1");Frame(hash,version);return hash;
    }
    private static void AppendRecord(IncrementalHash hash,long sequence,string key,string payload)
    {Frame(hash,sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));Frame(hash,key);Frame(hash,payload);}
    private static void AppendSearch(IncrementalHash hash,long sequence,string key,string payload,SearchProjection p)
    {
        AppendRecord(hash,sequence,key,payload);Frame(hash,p.Family);Frame(hash,p.Label);Frame(hash,p.Search);Frame(hash,p.Fold);Frame(hash,p.SortKey);
        Frame(hash,p.GroupKey??"");Frame(hash,p.Language??"");Frame(hash,p.Codec??"");
        Frame(hash,p.Duration?.ToString("R",System.Globalization.CultureInfo.InvariantCulture)??"");
        Frame(hash,p.Channels?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"");
        Frame(hash,p.Availability.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Frame(hash,p.Completeness.ToString(System.Globalization.CultureInfo.InvariantCulture));Frame(hash,p.LookupKey??"");
    }
    private static void AppendKey(IncrementalHash hash,string key)=>Frame(hash,key);
    private static void Frame(IncrementalHash hash,string value)
    {
        var bytes=StrictUtf8.GetBytes(value);Span<byte> length=stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length,checked((ulong)bytes.Length));hash.AppendData(length);hash.AppendData(bytes);
    }
    private static string Finish(IncrementalHash hash)=>Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    private static string HashHead(string payload)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Frame(hash,"deviant-localization-generation-head-v1");Frame(hash,payload);return Finish(hash);
    }
    private static bool IsSha256(string value)
    {if(value.Length!=64)return false;try{return Convert.FromHexString(value).Length==32;}catch(FormatException){return false;}}

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if(StrictUtf8.GetByteCount(key)>4096)throw new InvalidDataException("Identity key exceeds its byte limit.");
    }
    private static void ValidateIdentity(LocalizationGenerationIdentity identity,StagedLocalizationGeneration writer)
    {
        ArgumentNullException.ThrowIfNull(identity);ValidateVersion(identity.InputSnapshotId,nameof(identity.InputSnapshotId));
        ValidateVersion(identity.ParsedResultId,nameof(identity.ParsedResultId));ValidateVersion(identity.RecordStoreVersion,nameof(identity.RecordStoreVersion));
        ValidateVersion(identity.SearchProjectionVersion,nameof(identity.SearchProjectionVersion));ValidateVersion(identity.JsonPolicyVersion,nameof(identity.JsonPolicyVersion));
        if(identity.RecordCount<0)throw new InvalidDataException("Published head record count is invalid.");
        if(identity.RecordStoreVersion!=writer.RecordStoreVersion||identity.SearchProjectionVersion!=writer.SearchProjectionVersion)
            throw new InvalidDataException("Published head uses another record or search contract.");
    }
    private static void ValidateStoredIdentity<T>(LocalizationGenerationIdentity identity,StoredHeader<T> stored,string version)
    {
        if(identity.RecordStoreVersion!=version||identity.SearchProjectionVersion!=stored.SearchVersion||identity.RecordCount!=stored.RecordCount||
           identity.InputSnapshotId!=stored.InputId||identity.ParsedResultId!=stored.ResultId||identity.JsonPolicyVersion!=stored.JsonVersion)
            throw new InvalidDataException("Generation head disagrees with its stored identity.");
    }
    private void ValidateWriter(StagedLocalizationGeneration writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if(!ReferenceEquals(writer.Owner,this)||!writer.Active)throw new InvalidOperationException("Localization generation writer is closed or belongs to another store.");
    }
    private void ValidateLease<T>(PublishedLocalizationGeneration<T> lease,bool recordsOnly)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if(!ReferenceEquals(lease.Owner,this)||!lease.Active)throw new InvalidOperationException("Localization generation lease is closed or belongs to another store.");
        if(!recordsOnly&&!lease.SearchProjectionMatches)throw new InvalidOperationException("Search projection is incompatible; rebuild it from admitted records.");
    }
    private static void ValidatePage(long cursor,int limit)
    {if(cursor< -1||limit is <1 or >LocalizationGenerationPageLimit)throw new ArgumentOutOfRangeException(nameof(limit));}
    private static void ValidateQuery(AssetQuery query)
    {ArgumentNullException.ThrowIfNull(query);if(query.Offset<0||query.Limit is <1 or >LocalizationGenerationCallerLimit)throw new ArgumentOutOfRangeException(nameof(query));}
    private static T Deserialize<T>(string json)=>JsonSerializer.Deserialize<T>(json)??throw new InvalidDataException("Generation row deserialized to null.");
    private void BindGroup(SqliteCommand command,string generation)
    {command.Parameters.AddWithValue("$r",revision);command.Parameters.AddWithValue("$g",generation);}
}
