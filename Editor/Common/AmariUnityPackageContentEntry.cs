namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public readonly struct AmariUnityPackageContentEntry
    {
        public string Guid { get; }
        public string Pathname { get; }
        public bool HasAsset { get; }
        public long AssetSize { get; }
        public string AssetSha256 { get; }
        public bool HasMeta { get; }
        public string MetaSha256 { get; }
        public string MetaGuid { get; }

        public AmariUnityPackageContentEntry(
            string guid,
            string pathname,
            bool hasAsset,
            long assetSize,
            string assetSha256,
            bool hasMeta,
            string metaSha256,
            string metaGuid)
        {
            Guid = guid ?? string.Empty;
            Pathname = pathname ?? string.Empty;
            HasAsset = hasAsset;
            AssetSize = assetSize;
            AssetSha256 = assetSha256 ?? string.Empty;
            HasMeta = hasMeta;
            MetaSha256 = metaSha256 ?? string.Empty;
            MetaGuid = metaGuid ?? string.Empty;
        }
    }
}
