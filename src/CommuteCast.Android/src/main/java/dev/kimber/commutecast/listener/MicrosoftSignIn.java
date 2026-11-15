package dev.kimber.commutecast.listener;

import android.app.Activity;
import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.Uri;
import android.os.Build;
import android.util.Base64;

import com.microsoft.identity.client.AuthenticationCallback;
import com.microsoft.identity.client.AcquireTokenSilentParameters;
import com.microsoft.identity.client.IAccount;
import com.microsoft.identity.client.IAuthenticationResult;
import com.microsoft.identity.client.ISingleAccountPublicClientApplication;
import com.microsoft.identity.client.PublicClientApplication;
import com.microsoft.identity.client.SignInParameters;
import com.microsoft.identity.client.exception.MsalException;
import com.microsoft.identity.client.exception.MsalUiRequiredException;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Collections;

final class MicrosoftSignIn {
    record Account(String key, String token) { }

    static final class SignInRequired extends Exception { }

    private final Context context;
    private String clientIdentity;
    private ISingleAccountPublicClientApplication client;

    MicrosoftSignIn(Context context) {
        this.context = context;
    }

    Account account(LibrarySettings settings) throws Exception {
        ISingleAccountPublicClientApplication app = client(settings);
        IAccount account = app.getCurrentAccount().getCurrentAccount();
        if (account == null) throw new SignInRequired();
        try {
            IAuthenticationResult result = app.acquireTokenSilent(new AcquireTokenSilentParameters.Builder()
                .withScopes(Collections.singletonList(settings.scope())).forAccount(account)
                .fromAuthority(settings.tenant().equals("common") ? account.getAuthority() : settings.authority())
                .build());
            IAccount authenticated = result.getAccount();
            if (authenticated == null || !account.getId().equals(authenticated.getId()))
                throw new SignInRequired();
            return new Account(settings.clientId() + "/" + account.getId(), result.getAccessToken());
        } catch (MsalUiRequiredException e) {
            throw new SignInRequired();
        }
    }

    record Interactive(ISingleAccountPublicClientApplication app, boolean alreadySignedIn, String scope) {
        void start(Activity activity, AuthenticationCallback callback) {
            SignInParameters request = SignInParameters.builder().withActivity(activity)
                .withScopes(Collections.singletonList(scope)).withCallback(callback).build();
            if (alreadySignedIn) app.signInAgain(request);
            else app.signIn(request);
        }
    }

    Interactive interactive(LibrarySettings settings) throws Exception {
        ISingleAccountPublicClientApplication app = client(settings);
        return new Interactive(app, app.getCurrentAccount().getCurrentAccount() != null, settings.scope());
    }

    void signOut(LibrarySettings settings) throws Exception {
        if (settings != null) client(settings).signOut();
    }

    String signatureHash() throws Exception {
        PackageManager manager = context.getPackageManager();
        String id = context.getPackageName();
        Signature[] signatures;
        if (Build.VERSION.SDK_INT >= 28) {
            PackageInfo info = manager.getPackageInfo(id, PackageManager.GET_SIGNING_CERTIFICATES);
            signatures = info.signingInfo.getApkContentsSigners();
        } else {
            signatures = manager.getPackageInfo(id, PackageManager.GET_SIGNATURES).signatures;
        }
        return Base64.encodeToString(MessageDigest.getInstance("SHA-1").digest(signatures[0].toByteArray()), Base64.NO_WRAP);
    }

    private synchronized ISingleAccountPublicClientApplication client(LibrarySettings settings) throws Exception {
        String identity = settings.clientId() + "/" + settings.tenant();
        if (identity.equals(clientIdentity)) return client;

        JSONObject audience = new JSONObject().put("type", "AzureADandPersonalMicrosoftAccount");
        if (!settings.tenant().equals("common"))
            audience.put("type", "AzureADMyOrg").put("tenant_id", settings.tenant());
        JSONObject authority = new JSONObject().put("type", "AAD").put("audience", audience);
        String redirect = new Uri.Builder().scheme("msauth").authority(context.getPackageName())
            .appendPath(signatureHash()).build().toString();
        JSONObject config = new JSONObject().put("client_id", settings.clientId())
            .put("redirect_uri", redirect).put("account_mode", "SINGLE")
            .put("broker_redirect_uri_registered", true)
            .put("authorities", new JSONArray().put(authority));
        File file = File.createTempFile("microsoft-auth-", ".json", context.getCacheDir());
        try {
            try (FileOutputStream output = new FileOutputStream(file)) {
                output.write(config.toString().getBytes(StandardCharsets.UTF_8));
            }
            var initialized = PublicClientApplication.createSingleAccountPublicClientApplication(context, file);
            clientIdentity = identity;
            client = initialized;
            return client;
        } finally {
            file.delete();
        }
    }

    static String issue(Throwable error) {
        if (error instanceof SignInRequired) return "Connect your Microsoft account to load the listening folder.";
        if (error instanceof MsalException e) return "Microsoft sign-in failed (" + e.getErrorCode() + "). Check the account and app registration.";
        if (error instanceof java.io.IOException && error.getMessage() != null &&
            !error.getMessage().contains("://")) return error.getMessage();
        return "Couldn’t open the OneDrive library. Check your account, folder and connection.";
    }
}
