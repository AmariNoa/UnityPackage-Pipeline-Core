namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public interface IAmariUnityPackageImportPipelineHook
    {
        void OnBeforePackageImport(AmariUnityPackageImportContext context);
        void OnPackageImportCompleted(AmariUnityPackageImportContext context);
        void OnImportRequestFinalized(AmariUnityPackageImportResultContext context);
    }
}
