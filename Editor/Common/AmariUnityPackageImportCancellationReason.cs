namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public enum AmariUnityPackageImportCancellationReason
    {
        None = 0,
        UnityCancelledEvent = 1,
        WindowClosedFallback = 2,
        PipelineReset = 3,
        HangTimeoutAfterImportConfirm = 4,
        StaleRecovery = 5
    }
}
