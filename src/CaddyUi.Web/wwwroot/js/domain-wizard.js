(() => {
    const form = document.querySelector('[data-domain-form]');
    if (!form) {
        return;
    }

    const provider = form.querySelector('[data-domain-provider]');
    const automaticDns = form.querySelector('[data-domain-auto-dns]');
    const automaticDnsFields = Array.from(form.querySelectorAll('[data-domain-auto-dns-field]'));
    if (!provider || !automaticDns) {
        return;
    }

    const updateAutomaticDnsFields = () => {
        const providerSelected = provider.value.length > 0;
        if (!providerSelected) {
            automaticDns.checked = false;
        }

        const enabled = providerSelected && automaticDns.checked;
        for (const field of automaticDnsFields) {
            field.disabled = !enabled;
        }
    };

    automaticDns.addEventListener('change', updateAutomaticDnsFields);

    provider.addEventListener('change', () => {
        automaticDns.checked = provider.value.length > 0;
        updateAutomaticDnsFields();
    });

    updateAutomaticDnsFields();
})();
