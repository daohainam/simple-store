(function () {
    if (!window.PublicKeyCredential) {
        return;
    }

    async function postJson(url, token, body) {
        const init = {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'RequestVerificationToken': token
            }
        };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        const res = await fetch(url, init);
        if (!res.ok) {
            let msg = res.statusText;
            try {
                const data = await res.json();
                if (data && data.error) msg = data.error;
            } catch { /* ignore */ }
            throw new Error(msg || 'Request failed');
        }
        if (res.status === 204) return null;
        return res.json();
    }

    async function postForOptions(url, token) {
        const res = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'RequestVerificationToken': token }
        });
        if (!res.ok) {
            throw new Error('Could not load passkey options.');
        }
        return res.json();
    }

    function credentialToJSON(credential) {
        // Use built-in serializer when available; fall back to manual base64url encoding.
        if (typeof credential.toJSON === 'function') {
            try { return credential.toJSON(); } catch { /* fall through */ }
        }
        const toB64u = (buf) => {
            if (!buf) return undefined;
            const bytes = new Uint8Array(buf);
            let s = '';
            for (let i = 0; i < bytes.byteLength; i++) s += String.fromCharCode(bytes[i]);
            return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
        };
        const r = credential.response;
        const json = {
            id: credential.id,
            rawId: toB64u(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
            response: {
                clientDataJSON: toB64u(r.clientDataJSON)
            }
        };
        if ('attestationObject' in r) {
            json.response.attestationObject = toB64u(r.attestationObject);
            json.response.transports = r.getTransports?.() ?? undefined;
            json.response.publicKey = toB64u(r.getPublicKey?.() ?? undefined);
            json.response.publicKeyAlgorithm = r.getPublicKeyAlgorithm?.() ?? undefined;
            json.response.authenticatorData = toB64u(r.getAuthenticatorData?.() ?? undefined);
        } else {
            json.response.authenticatorData = toB64u(r.authenticatorData);
            json.response.signature = toB64u(r.signature);
            json.response.userHandle = toB64u(r.userHandle);
        }
        return json;
    }

    async function register(token, name) {
        const optionsJson = await postForOptions('?handler=CreationOptions', token);
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(optionsJson);
        const credential = await navigator.credentials.create({ publicKey: options });
        if (!credential) throw new Error('No credential returned.');
        await postJson('?handler=Register', token, {
            credential: credentialToJSON(credential),
            name: name || ''
        });
    }

    async function signIn(token, returnUrl) {
        const optionsJson = await postForOptions('?handler=PasskeyRequestOptions', token);
        const options = PublicKeyCredential.parseRequestOptionsFromJSON(optionsJson);
        const credential = await navigator.credentials.get({ publicKey: options });
        if (!credential) throw new Error('No credential returned.');
        const url = '?handler=PasskeyAssert' + (returnUrl ? '&returnUrl=' + encodeURIComponent(returnUrl) : '');
        const result = await postJson(url, token, credentialToJSON(credential));
        return result.redirectTo || '/';
    }

    window.simpleStorePasskeys = { register, signIn };
})();
