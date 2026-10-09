namespace StreamHelper.Ui;

public interface IPositionable
{
    bool IsPositioning { get; }

    void BeginPositioning();

    void EndPositioning();

    void ResetPosition();
}
