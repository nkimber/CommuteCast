package dev.kimber.commutecast.listener;

import java.util.Locale;
import java.util.regex.Pattern;

record LibrarySettings(String clientId, String tenant, String path, String driveId, String folderId, boolean shared) {
    static final String DEFAULT_PATH = "CommuteCast/Ready to Listen";
    private static final Pattern GUID = Pattern.compile("[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}");
    private static final Pattern ITEM_ID = Pattern.compile("[A-Za-z0-9!_.-]{1,250}");

    LibrarySettings {
        clientId = clientId.trim().toLowerCase(Locale.ROOT);
        tenant = tenant.trim().toLowerCase(Locale.ROOT);
        if (tenant.isEmpty()) tenant = "common";
        path = path.trim();
        driveId = driveId.trim();
        folderId = folderId.trim();
        if (!GUID.matcher(clientId).matches()) throw new IllegalArgumentException("Enter the Android application (client) ID.");
        if (!tenant.equals("common") && !GUID.matcher(tenant).matches())
            throw new IllegalArgumentException("Enter a directory (tenant) ID, or leave it blank.");
        if (!driveId.isEmpty() || !folderId.isEmpty()) {
            if (!validItemId(driveId) || !validItemId(folderId))
                throw new IllegalArgumentException("Enter both the OneDrive drive ID and folder item ID.");
        } else {
            if (path.isEmpty()) path = DEFAULT_PATH;
            if (path.length() > 400) throw new IllegalArgumentException("Enter the listening folder’s OneDrive path.");
            for (String segment : path.split("/", -1)) {
                if (segment.trim().isEmpty() || segment.equals(".") || segment.equals("..") ||
                    segment.matches(".*[\\\\:*?\"<>|\\p{Cntrl}].*"))
                    throw new IllegalArgumentException("Separate the OneDrive folder names with slashes.");
            }
        }
    }

    static boolean validItemId(String id) {
        return id != null && ITEM_ID.matcher(id).matches() && !id.equals(".") && !id.equals("..");
    }

    static String migrateLegacyPath(String path, String driveId, String folderId) {
        return path.equalsIgnoreCase("Ready to Listen") && driveId.isEmpty() && folderId.isEmpty()
            ? DEFAULT_PATH : path;
    }

    String scope() {
        return shared ? "https://graph.microsoft.com/Files.Read.All" : "https://graph.microsoft.com/Files.Read";
    }

    String authority() {
        return "https://login.microsoftonline.com/" + tenant;
    }
}
