namespace AndroCp.Core;

public enum DeviceConnectionState { Checking, Ready, Unauthorized, Offline, Missing, NoSelection, Unavailable, Failed }

public sealed record DeviceConnection(DeviceConnectionState State, string Message)
{
    public bool CanCopy => State == DeviceConnectionState.Ready;
    public static DeviceConnection Checking { get; } = new(DeviceConnectionState.Checking, "Vérification de l’accès ADB au téléphone sélectionné…");
    public static DeviceConnection NoSelection(bool hasDevices) => new(DeviceConnectionState.NoSelection,
        hasDevices ? "Sélectionnez un téléphone dans la liste." : "Aucun appareil détecté. Vérifiez le câble USB, les pilotes et le débogage USB.");
}
