window.visualizer = {
    getLinePositions: function (canvasSelector) {
        const canvas = document.querySelector(canvasSelector);
        if (!canvas) return null;

        const canvasRect = canvas.getBoundingClientRect();
        const partitionRows = canvas.querySelectorAll('.partition-row');
        const consumerRows = canvas.querySelectorAll('.consumer-row');

        const partitions = [];
        partitionRows.forEach((row, index) => {
            const rect = row.getBoundingClientRect();
            partitions.push({
                index: index,
                leftX: rect.left - canvasRect.left,
                rightX: rect.right - canvasRect.left,
                centerY: rect.top - canvasRect.top + rect.height / 2
            });
        });

        const consumers = [];
        consumerRows.forEach((row, index) => {
            const rect = row.getBoundingClientRect();
            consumers.push({
                index: index,
                leftX: rect.left - canvasRect.left,
                rightX: rect.right - canvasRect.left,
                centerY: rect.top - canvasRect.top + rect.height / 2
            });
        });

        return {
            canvasWidth: canvasRect.width,
            canvasHeight: canvasRect.height,
            partitions: partitions,
            consumers: consumers
        };
    }
};
