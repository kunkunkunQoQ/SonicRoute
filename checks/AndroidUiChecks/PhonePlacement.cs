using System.Reflection;
using System.Windows;
using SonicRoute;

internal static partial class Program
{
    private static void CheckPhonePlacement()
    {
        var phoneType = typeof(App).Assembly.GetType("SonicRoute.PhoneCompanionLayout")!;
        var phoneArrange = phoneType.GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!;
        var work = new Rect(0, 0, 1200, 900);
        const double phoneWidth = 235;

        Rect PlacePhone(Rect owner) => (Rect)phoneArrange.Invoke(null, new object[] { owner, work, phoneWidth })!;
        bool Inside(Rect bounds) => bounds.Left >= work.Left - .01 && bounds.Top >= work.Top - .01
            && bounds.Right <= work.Right + .01 && bounds.Bottom <= work.Bottom + .01;

        var upperLeft = new Rect(0, 40, 300, 500);
        var phoneRight = PlacePhone(upperLeft);
        Check(phoneRight.Left >= upperLeft.Right + 5.9 && Inside(phoneRight),
            "phone companion opens to the right of a top-left main panel");

        var upperRight = new Rect(900, 40, 300, 500);
        var phoneLeft = PlacePhone(upperRight);
        Check(phoneLeft.Right <= upperRight.Left - 5.9 && Inside(phoneLeft),
            "phone companion opens to the left of a top-right main panel");

        var lowerLeft = PlacePhone(new Rect(0, 800, 300, 150));
        var lowerRight = PlacePhone(new Rect(900, 800, 300, 150));
        Check(Inside(lowerLeft) && Inside(lowerRight) && Math.Abs(lowerLeft.Bottom - work.Bottom) < .01
            && Math.Abs(lowerRight.Bottom - work.Bottom) < .01,
            "phone companion stays within the work area at both lower screen corners");

        var ownerBeforeMove = new Rect(300, 180, 300, 500);
        var ownerAfterMove = new Rect(380, 245, 300, 500);
        var companionBeforeMove = PlacePhone(ownerBeforeMove);
        var companionAfterMove = PlacePhone(ownerAfterMove);
        Check(Math.Abs((companionAfterMove.Left - companionBeforeMove.Left) - 80) < .01
            && Math.Abs((companionAfterMove.Top - companionBeforeMove.Top) - 65) < .01,
            "companion geometry follows the main panel's horizontal and vertical movement");

        var popupType = typeof(App).Assembly.GetType("SonicRoute.OutputVolumePopupLayout")!;
        var popupArrange = popupType.GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!;
        var popupWork = new Rect(0, 0, 1600, 1000);
        var popupOwner = new Rect(100, 200, 300, 400);
        var occupiedPhone = new Rect(412, 200, 235, 400);
        Rect PlacePopup(Rect owner, Size desired, Rect? occupied) => (Rect)popupArrange.Invoke(null,
            new object?[] { owner, popupWork, desired, occupied })!;

        var below = PlacePopup(popupOwner, new Size(300, 250), occupiedPhone);
        Check(below.Top >= popupOwner.Bottom + 5.9 && !below.IntersectsWith(occupiedPhone)
            && below.Left >= popupWork.Left && below.Right <= popupWork.Right && below.Bottom <= popupWork.Bottom,
            "extra panel prefers the free area below the owner and avoids the occupied phone companion");

        var lowOwner = new Rect(100, 700, 300, 200);
        var lowPhone = new Rect(412, 700, 235, 200);
        var movedAbove = PlacePopup(lowOwner, new Size(300, 300), lowPhone);
        Check(movedAbove.Bottom <= lowOwner.Top - 5.9 && !movedAbove.IntersectsWith(lowPhone)
            && movedAbove.Left >= popupWork.Left && movedAbove.Right <= popupWork.Right
            && movedAbove.Top >= popupWork.Top && movedAbove.Bottom <= popupWork.Bottom,
            "extra panel moves above when the lower area is too short, remains on-screen and avoids the phone panel");
    }
}
